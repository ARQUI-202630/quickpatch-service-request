using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Payments;

namespace QuickPatch.ServiceRequest.Infrastructure.Messaging;

/// <summary>
/// Consumidor de los eventos de Payments (quickpatch-kafka v0.9.0): <c>payment.approved</c> y <c>payment.rejected</c> v1.
/// Igual que <see cref="MatchingEventsConsumer"/>: confirma el offset solo después de aplicar el evento, descarta el mensaje
/// que no cumple el contrato y reintenta los errores de lectura no fatales.
/// </summary>
public sealed partial class PaymentEventsConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<PaymentEventsConsumer> logger) : BackgroundService
{
    public static readonly string[] Topics = [EventTypes.PaymentApproved, EventTypes.PaymentRejected];

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => ConsumeLoopAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(Topics);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    // Por ejemplo, el topic todavía no existe porque Payments no ha publicado.
                    LogConsumeFailed(logger, ex.Error.Reason, ex);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo leer de los topics de Payments ({Reason}); se reintentará.")]
    private static partial void LogConsumeFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}