using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Categories;

namespace QuickPatch.ServiceRequest.Application.Categories;

public enum CategoryChangeOutcome
{
    /// <summary>La réplica se creó o se actualizó.</summary>
    Applied,

    /// <summary>El evento ya se había aplicado (mismo <c>eventId</c>, RN-EV1).</summary>
    Duplicate,

    /// <summary>El evento es más antiguo que la copia guardada.</summary>
    Stale,
}

/// <summary>
/// Consumidor de <c>catalog.category-changed</c>: mantiene la réplica local de categorías.
/// Es idempotente por <c>eventId</c> y aplica el cambio bajo el tenant del evento (DD, sección 10.3).
/// </summary>
public sealed partial class ApplyCategoryChangedHandler(
    ITenantUnitOfWork unitOfWork,
    ICategoryReplicaRepository categories,
    IProcessedEventRepository processedEvents,
    TimeProvider clock,
    ILogger<ApplyCategoryChangedHandler> logger)
{
    public Task<CategoryChangeOutcome> HandleAsync(EventEnvelope<CategoryChangedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return unitOfWork.ExecuteAsync(
            envelope.TenantId,
            async ct =>
            {
                if (await processedEvents.ExistsAsync(envelope.EventId, ct))
                {
                    LogDuplicate(logger, envelope.EventId);
                    return CategoryChangeOutcome.Duplicate;
                }

                processedEvents.Add(envelope.EventId, envelope.TenantId, envelope.EventType, clock.GetUtcNow());

                var data = envelope.Data;
                var existing = await categories.FindAsync(data.CategoryId, ct);
                if (existing is null)
                {
                    categories.Add(new CategoryReplica(data.CategoryId, envelope.TenantId, data.Name, data.Active, data.UpdatedAt));
                    return CategoryChangeOutcome.Applied;
                }

                return existing.ApplyChange(data.Name, data.Active, data.UpdatedAt)
                    ? CategoryChangeOutcome.Applied
                    : CategoryChangeOutcome.Stale;
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento {EventId} ya aplicado; se descarta.")]
    private static partial void LogDuplicate(ILogger logger, Guid eventId);
}