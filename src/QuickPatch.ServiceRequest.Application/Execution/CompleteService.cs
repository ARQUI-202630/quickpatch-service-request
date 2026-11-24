using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary>
/// El técnico asignado completa el servicio (RF-15, DD 8.3.11). Con la fila de la solicitud bloqueada cuenta las evidencias
/// (RN-SR3: en la misma transacción, para no cruzarse con una subida), pasa <c>en_progreso</c> a <c>completado</c> y deja en
/// el Outbox <c>service-request.completed</c>, con el total de la cotización aceptada (RN-P4, RN-M6), y
/// <c>service-request.status-changed</c>.
/// </summary>
public sealed class CompleteServiceHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IEvidenceRepository evidence,
    IQuoteRepository quotes,
    IOutbox outbox,
    TimeProvider clock)
{
    public async Task<ServiceStepResult> HandleAsync(ServiceStepCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await unitOfWork.ExecuteAsync<ServiceStepResult>(
            command.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                if (request is null || request.TechnicianId != command.TechnicianId)
                {
                    return new ServiceStepResult.NotFound();
                }

                if (request.Status != ServiceRequestStatus.EnProgreso)
                {
                    return new ServiceStepResult.InvalidTransition(request.Status);
                }

                if (await evidence.CountAsync(request.Id, ct) == 0)
                {
                    return new ServiceStepResult.EvidenceRequired();
                }

                // en_progreso solo se alcanza desde cotizacion_aceptada (RN-SR6), así que hay una aceptada (RN-Q4).
                var accepted = (await quotes.ListAsync(request.Id, ct)).SingleOrDefault(q => q.Status == QuoteStatus.Aceptada)
                    ?? throw new InvalidOperationException($"La solicitud {request.Id} está en progreso sin cotización aceptada.");

                var now = clock.GetUtcNow();
                var previousStatus = request.Status;
                request.TryComplete(now);
                outbox.Enqueue(ServiceRequestEvents.Completed(request, accepted, command.CorrelationId, now));
                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, command.CorrelationId, now));
                return new ServiceStepResult.Done(request);
            },
            cancellationToken);
    }
}