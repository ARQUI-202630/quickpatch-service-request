using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Tenants;

namespace QuickPatch.ServiceRequest.Application.Tenants;

public enum TenantStatusChangeOutcome
{
    /// <summary>La réplica se creó o se actualizó.</summary>
    Applied,

    /// <summary>El evento ya se había aplicado (mismo <c>eventId</c>, RN-EV1).</summary>
    Duplicate,

    /// <summary>El evento es más antiguo que la copia guardada (ADR-017).</summary>
    Stale,
}

/// <summary>
/// Consumidor de <c>tenant.status-changed</c>: mantiene la réplica local del estado de los tenants (RN-T1, SCRUM-510).
/// Es idempotente por <c>eventId</c> y aplica el cambio bajo el tenant del evento, que es el que cambió de estado (DD, sección 10.3).
/// </summary>
public sealed partial class ApplyTenantStatusChangedHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantStatusReplicaRepository tenants,
    IProcessedEventRepository processedEvents,
    TimeProvider clock,
    ILogger<ApplyTenantStatusChangedHandler> logger)
{
    public Task<TenantStatusChangeOutcome> HandleAsync(EventEnvelope<TenantStatusChangedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return unitOfWork.ExecuteAsync(
            envelope.TenantId,
            async ct =>
            {
                if (await processedEvents.ExistsAsync(envelope.EventId, ct))
                {
                    LogDuplicate(logger, envelope.EventId);
                    return TenantStatusChangeOutcome.Duplicate;
                }

                processedEvents.Add(envelope.EventId, envelope.TenantId, envelope.EventType, clock.GetUtcNow());

                var data = envelope.Data;
                var existing = await tenants.FindAsync(envelope.TenantId, ct);
                if (existing is null)
                {
                    tenants.Add(new TenantStatusReplica(envelope.TenantId, data.Status, data.UpdatedAt));
                    return TenantStatusChangeOutcome.Applied;
                }

                return existing.ApplyChange(data.Status, data.UpdatedAt)
                    ? TenantStatusChangeOutcome.Applied
                    : TenantStatusChangeOutcome.Stale;
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento {EventId} ya aplicado; se descarta.")]
    private static partial void LogDuplicate(ILogger logger, Guid eventId);
}