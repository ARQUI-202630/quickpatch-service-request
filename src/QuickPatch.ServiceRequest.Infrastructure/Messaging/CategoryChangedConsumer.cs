using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Categories;
using QuickPatch.ServiceRequest.Application.Events;

namespace QuickPatch.ServiceRequest.Infrastructure.Messaging;

/// <summary>
/// Consumidor de <c>catalog.category-changed</c> v1. Confirma el offset solo después de aplicar el evento;
/// si el procesamiento falla, vuelve a leer el mismo mensaje. Un mensaje que no cumple el contrato se registra
/// y se salta, para no bloquear la partición.
/// </summary>
public sealed partial class CategoryChangedConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<CategoryChangedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => ConsumeLoopAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(EventTypes.CategoryChanged);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result is null)
                {
                    continue;
                }

                if (await TryProcessAsync(result.Message.Value, stoppingToken))
                {
                    consumer.Commit(result);
                }
                else
                {
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    /// <summary>Aplica un mensaje; devuelve false si hay que reintentarlo.</summary>
    public async Task<bool> TryProcessAsync(string value, CancellationToken cancellationToken)
    {
        EventEnvelope<CategoryChangedData>? envelope;
        try
        {
            envelope = EventSerializer.Deserialize<EventEnvelope<CategoryChangedData>>(value);
        }
        catch (JsonException ex)
        {
            LogInvalidMessage(logger, ex);
            return true;
        }

        if (envelope is null || envelope.EventType != EventTypes.CategoryChanged || envelope.EventVersion != 1
            || envelope.TenantId == Guid.Empty || envelope.Data is null || envelope.Data.CategoryId == Guid.Empty)
        {
            LogInvalidMessage(logger, null);
            return true;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ApplyCategoryChangedHandler>();
            await handler.HandleAsync(envelope, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessingFailed(logger, envelope.EventId, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje de catalog.category-changed que no cumple el contrato; se descarta.")]
    private static partial void LogInvalidMessage(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}