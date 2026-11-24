using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary>
/// El técnico asignado inicia el servicio (RF-14): con la fila de la solicitud bloqueada, <c>cotizacion_aceptada</c> pasa a
/// <c>en_progreso</c> (RN-SR6) y quedan en el Outbox <c>service-request.started</c> y <c>service-request.status-changed</c>.
/// </summary>
public sealed class StartServiceHandler(
    ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, IOutbox outbox, TimeProvider clock)
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

                var now = clock.GetUtcNow();
                var previousStatus = request.Status;
                if (!request.TryStart(now))
                {
                    return new ServiceStepResult.InvalidTransition(request.Status);
                }

                outbox.Enqueue(ServiceRequestEvents.Started(request, command.CorrelationId, now));
                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, command.CorrelationId, now));
                return new ServiceStepResult.Done(request);
            },
            cancellationToken);
    }
}