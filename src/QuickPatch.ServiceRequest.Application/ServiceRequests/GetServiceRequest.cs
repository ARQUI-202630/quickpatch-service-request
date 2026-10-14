using QuickPatch.ServiceRequest.Application.Abstractions;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>Quién consulta el detalle. Todo sale del JWT; <see cref="AccessToken"/> se reenvía a Identity tal cual.</summary>
public sealed record ServiceRequestViewer(Guid UserId, Guid TenantId, string Role, string AccessToken, Guid CorrelationId)
{
    public const string ClientRole = "cliente";
    public const string TechnicianRole = "tecnico";
}

/// <summary>Bloque <c>technician</c> del contrato.</summary>
public sealed record TechnicianView(Guid Id, string? FullName);

/// <summary>Bloque <c>client</c> del contrato; solo para el técnico asignado (RN-SR13).</summary>
public sealed record ClientView(Guid Id, string? FullName, string? Phone);

public abstract record GetServiceRequestResult
{
    public sealed record Found(DomainServiceRequest ServiceRequest, TechnicianView? Technician, ClientView? Client) : GetServiceRequestResult;

    /// <summary>No existe, es de otro tenant (RLS), de otro cliente o de otro técnico: 404, para no revelar que existe.</summary>
    public sealed record NotFound : GetServiceRequestResult;

    /// <summary>El técnico asignado la consulta fuera de <c>asignado</c>–<c>completado</c> (RN-SR13), o el rol no consulta solicitudes.</summary>
    public sealed record Forbidden : GetServiceRequestResult;
}

/// <summary>
/// Detalle de una solicitud (<c>GET /v1/service-requests/{id}</c>, contrato 1.1.0, DD V4 8.3.4).
/// <list type="bullet">
/// <item>Cliente dueño: la solicitud completa; <c>technician</c> desde la asignación y <c>client</c> en null.</item>
/// <item>Técnico asignado: solo entre <c>asignado</c> y <c>completado</c>, con <c>client</c> (nombre y teléfono).</item>
/// </list>
/// Los nombres y el teléfono vienen de Identity después de cerrar la transacción; si Identity falla, llegan en null.
/// </summary>
public sealed class GetServiceRequestHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IUserDirectory users)
{
    public async Task<GetServiceRequestResult> HandleAsync(ServiceRequestViewer viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var isTechnician = viewer.Role == ServiceRequestViewer.TechnicianRole;
        if (!isTechnician && viewer.Role != ServiceRequestViewer.ClientRole)
        {
            return new GetServiceRequestResult.Forbidden();
        }

        var serviceRequest = await unitOfWork.ExecuteAsync(
            viewer.TenantId,
            ct => isTechnician
                ? serviceRequests.FindForTechnicianAsync(id, viewer.UserId, ct)
                : serviceRequests.FindForClientAsync(id, viewer.UserId, ct),
            cancellationToken);

        if (serviceRequest is null)
        {
            return new GetServiceRequestResult.NotFound();
        }

        if (isTechnician && !serviceRequest.IsVisibleToAssignedTechnician)
        {
            return new GetServiceRequestResult.Forbidden();
        }

        var ids = new List<Guid>();
        if (serviceRequest.TechnicianId is { } technicianId)
        {
            ids.Add(technicianId);
        }

        if (isTechnician)
        {
            ids.Add(serviceRequest.ClientId);
        }

        var contacts = ids.Count == 0
            ? new Dictionary<Guid, UserContact>()
            : await users.LookupAsync(ids, viewer.AccessToken, viewer.CorrelationId, cancellationToken);

        var technician = serviceRequest.TechnicianId is { } tid
            ? new TechnicianView(tid, contacts.GetValueOrDefault(tid)?.FullName)
            : null;

        ClientView? client = null;
        if (isTechnician)
        {
            var contact = contacts.GetValueOrDefault(serviceRequest.ClientId);
            client = new ClientView(serviceRequest.ClientId, contact?.FullName, contact?.Phone);
        }

        return new GetServiceRequestResult.Found(serviceRequest, technician, client);
    }
}