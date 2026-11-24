using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Tenants;
using QuickPatch.ServiceRequest.Domain.Tenants;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.ServiceRequest.Infrastructure.Messaging;

/// <summary>
/// Consumidor de <c>tenant.status-changed</c> v1 (Identity, RN-T1, SCRUM-510): alimenta la réplica del estado de los
/// tenants. Confirma el offset solo después de aplicar el evento;
/// si el procesamiento falla, vuelve a leer el mismo mensaje. Un mensaje que no cumple el contrato se registra
/// y se salta, para no bloquear la partición. Un error de lectura no fatal (por ejemplo, el topic aún no existe)
/// se reintenta sin detener el servicio.
/// </summary>
public sealed partial class TenantStatusChangedConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<TenantStatusChangedConsumer> logger)
    : KafkaConsumerService(consumer, [EventTypes.TenantStatusChanged], logger)
{
    protected override Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken) =>
        TryProcessAsync(result.Message.Value, cancellationToken);

    /// <summary>Aplica un mensaje; devuelve false si hay que reintentarlo.</summary>
    public async Task<bool> TryProcessAsync(string value, CancellationToken cancellationToken)
    {
        EventEnvelope<TenantStatusChangedData>? envelope;
        try
        {
            envelope = EventSerializer.Deserialize<EventEnvelope<TenantStatusChangedData>>(value);
        }
        catch (JsonException ex)
        {
            LogInvalidMessage(logger, ex);
            return true;
        }

        if (envelope is null || envelope.EventType != EventTypes.TenantStatusChanged || envelope.EventVersion != 1
            || envelope.TenantId == Guid.Empty || envelope.Data is null || envelope.Data.TenantId != envelope.TenantId
            || !TenantStatusReplica.IsValidStatus(envelope.Data.Status))
        {
            LogInvalidMessage(logger, null);
            return true;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ApplyTenantStatusChangedHandler>();
            await handler.HandleAsync(envelope, cancellationToken);
            return true;
        }
        // Solo es un apagado si el token está cancelado. Un tiempo de espera con forma de cancelación (la base o un servicio que no
        // responde) es un fallo del mensaje: devuelve false para reintentarlo, en vez de subir hasta el bucle y terminarlo.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogProcessingFailed(logger, envelope.EventId, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje de tenant.status-changed que no cumple el contrato; se descarta.")]
    private static partial void LogInvalidMessage(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}