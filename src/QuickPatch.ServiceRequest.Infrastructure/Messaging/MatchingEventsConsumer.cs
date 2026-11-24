using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Matching;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.ServiceRequest.Infrastructure.Messaging;

/// <summary>
/// Consumidor de los eventos de Matching que cambian el estado de la solicitud (DD 7.4):
/// <c>matching.technician-assigned</c>, <c>matching.no-technician-available</c> y <c>matching.search-resumed</c> v1. Igual que
/// <see cref="CategoryChangedConsumer"/>: confirma el offset solo después de aplicar el evento, descarta el mensaje
/// que no cumple el contrato y reintenta los errores de lectura no fatales. Para sumar otro evento de Matching (por
/// ejemplo <c>matching.technician-assigned</c>) basta con agregarlo a <see cref="Topics"/> y a <see cref="DispatchAsync"/>.
/// </summary>
public sealed partial class MatchingEventsConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<MatchingEventsConsumer> logger)
    : KafkaConsumerService(consumer, Topics, logger)
{
    public static readonly string[] Topics = [EventTypes.TechnicianAssigned, EventTypes.NoTechnicianAvailable, EventTypes.SearchResumed];

    protected override Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken) =>
        TryProcessAsync(result.Message.Value, cancellationToken);

    /// <summary>Aplica un mensaje; devuelve false si hay que reintentarlo.</summary>
    public async Task<bool> TryProcessAsync(string value, CancellationToken cancellationToken)
    {
        EventEnvelope<JsonElement>? envelope;
        try
        {
            envelope = EventSerializer.Deserialize<EventEnvelope<JsonElement>>(value);
        }
        catch (JsonException ex)
        {
            LogInvalidMessage(logger, ex);
            return true;
        }

        if (envelope is null || envelope.EventVersion != 1 || envelope.EventId == Guid.Empty
            || envelope.TenantId == Guid.Empty || envelope.Data.ValueKind != JsonValueKind.Object)
        {
            LogInvalidMessage(logger, null);
            return true;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<MatchingStatusHandler>();
            return await DispatchAsync(handler, envelope, cancellationToken);
        }
        catch (JsonException ex)
        {
            LogInvalidMessage(logger, ex);
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

    private async Task<bool> DispatchAsync(
        MatchingStatusHandler handler, EventEnvelope<JsonElement> envelope, CancellationToken cancellationToken)
    {
        switch (envelope.EventType)
        {
            case EventTypes.TechnicianAssigned:
                var assigned = envelope.Data.Deserialize<TechnicianAssignedData>(EventSerializer.Options);
                if (assigned is null || assigned.ServiceRequestId == Guid.Empty || assigned.TechnicianId == Guid.Empty)
                {
                    LogInvalidMessage(logger, null);
                    return true;
                }

                await handler.HandleTechnicianAssignedAsync(Typed(envelope, assigned), cancellationToken);
                return true;

            case EventTypes.NoTechnicianAvailable:
                var noTechnician = envelope.Data.Deserialize<NoTechnicianAvailableData>(EventSerializer.Options);
                if (noTechnician is null || noTechnician.ServiceRequestId == Guid.Empty)
                {
                    LogInvalidMessage(logger, null);
                    return true;
                }

                await handler.HandleNoTechnicianAvailableAsync(Typed(envelope, noTechnician), cancellationToken);
                return true;

            case EventTypes.SearchResumed:
                var resumed = envelope.Data.Deserialize<SearchResumedData>(EventSerializer.Options);
                if (resumed is null || resumed.ServiceRequestId == Guid.Empty)
                {
                    LogInvalidMessage(logger, null);
                    return true;
                }

                await handler.HandleSearchResumedAsync(Typed(envelope, resumed), cancellationToken);
                return true;

            default:
                LogInvalidMessage(logger, null);
                return true;
        }
    }

    private static EventEnvelope<T> Typed<T>(EventEnvelope<JsonElement> envelope, T data) =>
        new(envelope.EventId, envelope.EventType, envelope.EventVersion, envelope.OccurredAt, envelope.CorrelationId,
            envelope.TenantId, envelope.Producer, data);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje de Matching que no cumple el contrato; se descarta.")]
    private static partial void LogInvalidMessage(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}