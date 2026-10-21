using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;

namespace QuickPatch.ServiceRequest.Application.Payments;

public enum PaymentEventOutcome
{
    /// <summary>La solicitud pasó a <c>pagado</c> y se registró <c>service-request.status-changed</c> en el Outbox.</summary>
    Paid,

    /// <summary>La solicitud no estaba en <c>completado</c> (por ejemplo, ya estaba pagada); no cambia.</summary>
    Ignored,

    /// <summary>Pago rechazado: la solicitud sigue en <c>completado</c> y el cliente puede reintentar (RN-SR5).</summary>
    Rejected,

    /// <summary>El evento ya se había aplicado (mismo <c>eventId</c>, RN-EV1).</summary>
    Duplicate,

    /// <summary>La solicitud no existe en el tenant del evento.</summary>
    NotFound,
}

/// <summary>
/// Consumidor de los eventos de Payments (DD 7.4, quickpatch-kafka v0.9.0): <c>payment.approved</c> pasa la solicitud de
/// <c>completado</c> a <c>pagado</c> (RN-SR4) y publica <c>service-request.status-changed</c> en la misma transacción, para
/// el aviso en tiempo real; <c>payment.rejected</c> no cambia el estado (RN-SR5). Es idempotente por <c>eventId</c> y aplica
/// el cambio bajo el tenant del evento (DD 10.3).
/// </summary>
public sealed partial class PaymentEventsHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IProcessedEventRepository processedEvents,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<PaymentEventsHandler> logger)
{
    public Task<PaymentEventOutcome> HandleApprovedAsync(EventEnvelope<PaymentApprovedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.CorrelationId, envelope.Data.ServiceRequestId,
            approved: true, cancellationToken);
    }

    public Task<PaymentEventOutcome> HandleRejectedAsync(EventEnvelope<PaymentRejectedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.CorrelationId, envelope.Data.ServiceRequestId,
            approved: false, cancellationToken);
    }

    private Task<PaymentEventOutcome> ApplyAsync(
        Guid eventId, Guid tenantId, string eventType, Guid correlationId, Guid serviceRequestId, bool approved, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            tenantId,
            async ct =>
            {
                if (await processedEvents.ExistsAsync(eventId, ct))
                {
                    return PaymentEventOutcome.Duplicate;
                }

                var now = clock.GetUtcNow();
                processedEvents.Add(eventId, tenantId, eventType, now);

                var request = await serviceRequests.FindByIdAsync(serviceRequestId, ct);
                if (request is null)
                {
                    LogNotFound(logger, eventId, serviceRequestId);
                    return PaymentEventOutcome.NotFound;
                }

                if (!approved)
                {
                    LogApplied(logger, eventType, serviceRequestId, request.Status);
                    return PaymentEventOutcome.Rejected;
                }

                var previousStatus = request.Status;
                if (!request.TryMarkPaid())
                {
                    LogApplied(logger, eventType, serviceRequestId, request.Status);
                    return PaymentEventOutcome.Ignored;
                }

                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, correlationId, now));
                LogApplied(logger, eventType, serviceRequestId, request.Status);
                return PaymentEventOutcome.Paid;
            },
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El evento de pago {EventId} es de la solicitud {ServiceRequestId}, que no existe en su tenant.")]
    private static partial void LogNotFound(ILogger logger, Guid eventId, Guid serviceRequestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EventType} aplicado a la solicitud {ServiceRequestId}: queda en {Status}.")]
    private static partial void LogApplied(ILogger logger, string eventType, Guid serviceRequestId, string status);
}