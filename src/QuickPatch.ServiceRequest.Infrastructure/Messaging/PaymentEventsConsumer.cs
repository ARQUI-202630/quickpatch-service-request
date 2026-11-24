using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Payments;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.ServiceRequest.Infrastructure.Messaging;

/// <summary>
/// Consumidor de los eventos de Payments (quickpatch-kafka v0.9.0): <c>payment.approved</c> y <c>payment.rejected</c> v1.
/// Igual que <see cref="MatchingEventsConsumer"/>: confirma el offset solo después de aplicar el evento, descarta el mensaje
/// que no cumple el contrato y reintenta los errores de lectura no fatales.
/// </summary>
public sealed partial class PaymentEventsConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<PaymentEventsConsumer> logger)
    : KafkaConsumerService(consumer, Topics, logger)
{
    public static readonly string[] Topics = [EventTypes.PaymentApproved, EventTypes.PaymentRejected];

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
            var handler = scope.ServiceProvider.GetRequiredService<PaymentEventsHandler>();
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
        PaymentEventsHandler handler, EventEnvelope<JsonElement> envelope, CancellationToken cancellationToken)
    {
        switch (envelope.EventType)
        {
            case EventTypes.PaymentApproved:
                var approved = envelope.Data.Deserialize<PaymentApprovedData>(EventSerializer.Options);
                if (approved is null || approved.ServiceRequestId == Guid.Empty)
                {
                    LogInvalidMessage(logger, null);
                    return true;
                }

                await handler.HandleApprovedAsync(Typed(envelope, approved), cancellationToken);
                return true;

            case EventTypes.PaymentRejected:
                var rejected = envelope.Data.Deserialize<PaymentRejectedData>(EventSerializer.Options);
                if (rejected is null || rejected.ServiceRequestId == Guid.Empty)
                {
                    LogInvalidMessage(logger, null);
                    return true;
                }

                await handler.HandleRejectedAsync(Typed(envelope, rejected), cancellationToken);
                return true;

            default:
                LogInvalidMessage(logger, null);
                return true;
        }
    }

    private static EventEnvelope<T> Typed<T>(EventEnvelope<JsonElement> envelope, T data) =>
        new(envelope.EventId, envelope.EventType, envelope.EventVersion, envelope.OccurredAt, envelope.CorrelationId,
            envelope.TenantId, envelope.Producer, data);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje de Payments que no cumple el contrato; se descarta.")]
    private static partial void LogInvalidMessage(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}