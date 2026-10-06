using System.Text;
using System.Text.RegularExpressions;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Infrastructure.Options;
using QuickPatch.ServiceRequest.Infrastructure.Persistence;

namespace QuickPatch.ServiceRequest.Infrastructure.Messaging;

/// <summary>
/// Publicador del Transactional Outbox (ADR-007). Lee los eventos pendientes de <c>outbox_events</c> con el rol
/// del publicador (DD, sección 10.2), los publica en el topic con el nombre del evento y la clave del agregado, y
/// marca <c>published_at</c>. Si Kafka falla, suma un intento y reintenta en la siguiente vuelta: ningún evento se pierde.
/// </summary>
public sealed partial class OutboxPublisher(
    IServiceScopeFactory scopes,
    IProducer<string, string> producer,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishPendingAsync(stoppingToken);
                if (published > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(logger, ex);
            }

            await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
        }
    }

    /// <summary>Publica un lote de eventos pendientes; devuelve cuántos quedaron publicados.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceRequestDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var role = options.Value.PublisherRole;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!RoleName().IsMatch(role))
            {
                throw new InvalidOperationException("Outbox:PublisherRole no es un nombre de rol válido.");
            }

#pragma warning disable EF1002 // El nombre del rol se validó con una expresión regular estricta; no admite parámetros.
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE \"{role}\"", cancellationToken);
#pragma warning restore EF1002
        }

        var batch = await db.OutboxEvents
            .FromSql($"SELECT * FROM outbox_events WHERE published_at IS NULL ORDER BY created_at LIMIT {options.Value.BatchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var record in batch)
        {
            try
            {
                await producer.ProduceAsync(record.EventType, ToMessage(record), cancellationToken);
                record.PublishedAt = clock.GetUtcNow();
                published++;
            }
            catch (ProduceException<string, string> ex)
            {
                record.Attempts++;
                LogProduceFailed(logger, record.Id, record.Attempts, ex);
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    private static Message<string, string> ToMessage(OutboxEventRecord record) => new()
    {
        Key = record.AggregateId.ToString(),
        Value = record.Payload,
        Headers = new Headers
        {
            { "eventId", Encoding.UTF8.GetBytes(record.Id.ToString()) },
            { "eventType", Encoding.UTF8.GetBytes(record.EventType) },
        },
    };

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la publicación del Outbox.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo publicar el evento {EventId} (intento {Attempt}).")]
    private static partial void LogProduceFailed(ILogger logger, Guid eventId, int attempt, Exception exception);
}