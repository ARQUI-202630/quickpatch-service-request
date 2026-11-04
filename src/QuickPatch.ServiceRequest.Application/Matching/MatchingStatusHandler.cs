using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Matching;

public enum MatchingEventHandlingOutcome
{
    /// <summary>La solicitud cambió de estado y se registró <c>service-request.status-changed</c> en el Outbox.</summary>
    Applied,

    /// <summary>La solicitud no estaba en el estado de origen (por ejemplo, ya se canceló); no cambia.</summary>
    Ignored,

    /// <summary>Ya se aplicó un evento de Matching más reciente (ADR-017).</summary>
    Stale,

    /// <summary>El evento ya se había aplicado (mismo <c>eventId</c>, RN-EV1).</summary>
    Duplicate,

    /// <summary>La solicitud no existe en el tenant del evento.</summary>
    NotFound,
}

/// <summary>
/// Consumidor de los eventos de Matching que cambian el estado de la solicitud (DD 7.4): con técnico pasa a
/// <c>asignado</c>, sin técnicos pasa a <c>en_espera</c> y al reanudarse vuelve a <c>buscando_tecnico</c>. Es idempotente por <c>eventId</c> y aplica
/// el cambio bajo el tenant del evento (DD 10.3), así que una solicitud de otro tenant no se encuentra.
/// Cuando el cambio se aplica, registra <c>service-request.status-changed</c> en el Outbox en la misma transacción; si el
/// evento es viejo, repetido o no corresponde al estado, no lo registra, y Communication no avisa (SCRUM-466).
/// </summary>
public sealed partial class MatchingStatusHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IProcessedEventRepository processedEvents,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<MatchingStatusHandler> logger)
{
    public Task<MatchingEventHandlingOutcome> HandleTechnicianAssignedAsync(
        EventEnvelope<TechnicianAssignedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.CorrelationId, envelope.Data.ServiceRequestId,
            request => request.ApplyTechnicianAssigned(envelope.Data.TechnicianId, envelope.Data.AssignedAt, envelope.OccurredAt),
            cancellationToken);
    }

    public Task<MatchingEventHandlingOutcome> HandleNoTechnicianAvailableAsync(
        EventEnvelope<NoTechnicianAvailableData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.CorrelationId, envelope.Data.ServiceRequestId,
            request => request.ApplyNoTechnicianAvailable(envelope.OccurredAt), cancellationToken);
    }

    public Task<MatchingEventHandlingOutcome> HandleSearchResumedAsync(
        EventEnvelope<SearchResumedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.CorrelationId, envelope.Data.ServiceRequestId,
            request => request.ApplySearchResumed(envelope.OccurredAt), cancellationToken);
    }

    private Task<MatchingEventHandlingOutcome> ApplyAsync(
        Guid eventId,
        Guid tenantId,
        string eventType,
        Guid correlationId,
        Guid serviceRequestId,
        Func<DomainServiceRequest, MatchingEventOutcome> apply,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            tenantId,
            async ct =>
            {
                if (await processedEvents.ExistsAsync(eventId, ct))
                {
                    LogDuplicate(logger, eventId);
                    return MatchingEventHandlingOutcome.Duplicate;
                }

                var now = clock.GetUtcNow();
                processedEvents.Add(eventId, tenantId, eventType, now);

                var request = await serviceRequests.FindByIdAsync(serviceRequestId, ct);
                if (request is null)
                {
                    LogNotFound(logger, eventId, serviceRequestId);
                    return MatchingEventHandlingOutcome.NotFound;
                }

                var previousStatus = request.Status;
                var outcome = apply(request) switch
                {
                    MatchingEventOutcome.Applied => MatchingEventHandlingOutcome.Applied,
                    MatchingEventOutcome.Ignored => MatchingEventHandlingOutcome.Ignored,
                    _ => MatchingEventHandlingOutcome.Stale,
                };
                if (outcome == MatchingEventHandlingOutcome.Applied)
                {
                    outbox.Enqueue(BuildStatusChangedEvent(request, previousStatus, correlationId, now));
                }

                LogApplied(logger, eventType, serviceRequestId, outcome, request.Status);
                return outcome;
            },
            cancellationToken);

    private static OutboxMessage BuildStatusChangedEvent(
        DomainServiceRequest request, string previousStatus, Guid correlationId, DateTimeOffset now)
    {
        var eventId = Guid.CreateVersion7(now);
        var envelope = new EventEnvelope<ServiceRequestStatusChangedData>(
            eventId,
            EventTypes.ServiceRequestStatusChanged,
            1,
            now,
            correlationId,
            request.TenantId,
            EventTypes.Producer,
            new ServiceRequestStatusChangedData(
                request.Id, request.ClientId, request.TechnicianId, previousStatus, request.Status, now));

        return new OutboxMessage(
            eventId, request.TenantId, request.Id, EventTypes.ServiceRequestStatusChanged, EventSerializer.Serialize(envelope));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento {EventId} ya aplicado; se descarta.")]
    private static partial void LogDuplicate(ILogger logger, Guid eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evento {EventId}: la solicitud {ServiceRequestId} no existe en el tenant del evento; se descarta.")]
    private static partial void LogNotFound(ILogger logger, Guid eventId, Guid serviceRequestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EventType} para la solicitud {ServiceRequestId}: {Outcome} (estado {Status}).")]
    private static partial void LogApplied(ILogger logger, string eventType, Guid serviceRequestId, MatchingEventHandlingOutcome outcome, string status);
}