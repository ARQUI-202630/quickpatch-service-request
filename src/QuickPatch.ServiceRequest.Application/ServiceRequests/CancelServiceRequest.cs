using System.Text.Json;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>
/// Cancelación pedida por un usuario (<c>POST /v1/service-requests/{id}/cancel</c>, contrato 1.5.0). El tenant, el usuario
/// y el rol salen del JWT; <see cref="IsTenantAdmin"/> distingue al <c>admin_tenant</c> del cliente.
/// </summary>
public sealed record CancelServiceRequestCommand(
    Guid TenantId, Guid UserId, bool IsTenantAdmin, Guid ServiceRequestId, string? Reason, Guid CorrelationId);

public abstract record CancelServiceRequestResult
{
    public sealed record Cancelled(DomainServiceRequest ServiceRequest) : CancelServiceRequestResult;

    /// <summary>No existe, es de otro tenant (RLS) o, para un cliente, es de otro cliente: 404 para no revelarla.</summary>
    public sealed record NotFound : CancelServiceRequestResult;

    /// <summary>El servicio ya inició o la solicitud ya terminó o se canceló (RN-SR7).</summary>
    public sealed record InvalidTransition(string Status) : CancelServiceRequestResult;
}

/// <summary>
/// Cancela la solicitud antes de iniciar el servicio (RF-36, RN-SR7, RN-SR8; DD 8.3.9). En una sola transacción bajo el
/// tenant del token: bloquea la fila, aplica la cancelación, deja en el Outbox <c>service-request.cancelled</c> (Matching
/// retira la oferta y libera al técnico, RN-M5 y RN-M6) y <c>service-request.status-changed</c> (aviso en tiempo real),
/// y, si cancela un <c>admin_tenant</c>, escribe <c>cancelar_solicitud_admin</c> en <c>audit_logs</c>.
/// </summary>
public sealed class CancelServiceRequestHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IOutbox outbox,
    IAuditLog auditLog,
    TimeProvider clock)
{
    public const string AuditAction = "cancelar_solicitud_admin";

    public async Task<CancelServiceRequestResult> HandleAsync(CancelServiceRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Primero el motivo: una petición inválida no abre transacción ni bloquea la fila.
        var reason = DomainServiceRequest.ValidateCancellationReason(command.Reason);

        return await unitOfWork.ExecuteAsync<CancelServiceRequestResult>(
            command.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                if (request is null || (!command.IsTenantAdmin && request.ClientId != command.UserId))
                {
                    return new CancelServiceRequestResult.NotFound();
                }

                var now = clock.GetUtcNow();
                var previousStatus = request.Status;
                var actor = command.IsTenantAdmin ? CancellationActor.AdminTenant : CancellationActor.Cliente;
                if (!request.TryCancel(command.UserId, actor, reason, now))
                {
                    return new CancelServiceRequestResult.InvalidTransition(request.Status);
                }

                outbox.Enqueue(BuildCancelledEvent(request, command.CorrelationId, now));
                outbox.Enqueue(BuildStatusChangedEvent(request, previousStatus, command.CorrelationId, now));
                if (command.IsTenantAdmin)
                {
                    auditLog.Add(new AuditEntry(
                        Guid.CreateVersion7(now),
                        request.TenantId,
                        command.UserId,
                        AuditAction,
                        request.Id,
                        JsonSerializer.Serialize(new { previousStatus, correlationId = command.CorrelationId }),
                        now));
                }

                return new CancelServiceRequestResult.Cancelled(request);
            },
            cancellationToken);
    }

    private static OutboxMessage BuildCancelledEvent(DomainServiceRequest request, Guid correlationId, DateTimeOffset now)
    {
        var eventId = Guid.CreateVersion7(now);
        var envelope = new EventEnvelope<ServiceRequestCancelledData>(
            eventId,
            EventTypes.ServiceRequestCancelled,
            1,
            now,
            correlationId,
            request.TenantId,
            EventTypes.Producer,
            new ServiceRequestCancelledData(
                request.Id, request.ClientId, request.TechnicianId, request.CancelledByType!, request.CancellationReason!, now));
        return new OutboxMessage(eventId, request.TenantId, request.Id, EventTypes.ServiceRequestCancelled, EventSerializer.Serialize(envelope));
    }

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
            new ServiceRequestStatusChangedData(request.Id, request.ClientId, request.TechnicianId, previousStatus, request.Status, now));
        return new OutboxMessage(
            eventId, request.TenantId, request.Id, EventTypes.ServiceRequestStatusChanged, EventSerializer.Serialize(envelope));
    }
}