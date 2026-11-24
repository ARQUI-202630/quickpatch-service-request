using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>
/// Lista de <c>GET /v1/service-requests</c>: todas las del tenant para el <c>admin_tenant</c> (contrato 1.4.0) o, con
/// <see cref="Scope"/>, las del cliente o las de su empresa (contrato 1.9.0, RN-SR14).
/// </summary>
public sealed record ListTenantServiceRequestsQuery(Guid TenantId, string? Status, int? Page, int? Size, ListScope? Scope = null);

public sealed record TenantServiceRequestPage(IReadOnlyList<DomainServiceRequest> Items, int Page, int Size, int TotalItems);

/// <summary>
/// Solicitudes del tenant del token, de la más reciente a la más antigua (RF-18, SCRUM-38). Corre bajo el tenant del token
/// (RLS); la API entrega solo estado, identificadores y fechas, sin dirección, ubicación ni contacto (DD 8.3.15).
/// </summary>
public sealed class ListTenantServiceRequestsHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests)
{
    public async Task<TenantServiceRequestPage> HandleAsync(ListTenantServiceRequestsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (page, size) = ListFilters.Validate(query.Status, query.Page, query.Size);

        var (items, total) = await unitOfWork.ExecuteAsync(
            query.TenantId,
            ct => serviceRequests.ListForTenantAsync(query.Status, query.Scope, (page - 1) * size, size, ct),
            cancellationToken);
        return new TenantServiceRequestPage(items, page, size, total);
    }
}