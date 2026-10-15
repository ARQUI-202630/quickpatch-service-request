using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>Lista del técnico (<c>GET /v1/technicians/me/service-requests</c>, contrato 1.2.0). Todo sale del JWT salvo los filtros.</summary>
public sealed record ListTechnicianServiceRequestsQuery(Guid TenantId, Guid TechnicianId, string? Status, int? Page, int? Size);

public sealed record TechnicianServiceRequestPage(IReadOnlyList<DomainServiceRequest> Items, int Page, int Size, int TotalItems);

/// <summary>
/// Solicitudes asignadas al técnico del token, de la asignación más reciente a la más antigua (RF-17, SCRUM-464). Corre
/// bajo el tenant del token (RLS). La API entrega solo los campos sin contacto (RN-SR13); el detalle sigue en
/// <see cref="GetServiceRequestHandler"/>.
/// </summary>
public sealed class ListTechnicianServiceRequestsHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests)
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public async Task<TechnicianServiceRequestPage> HandleAsync(ListTechnicianServiceRequestsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = query.Page ?? 1;
        var size = query.Size ?? DefaultSize;
        var errors = new Dictionary<string, string[]>();
        if (query.Status is not null && !ServiceRequestStatus.All.Contains(query.Status))
        {
            errors["status"] = ["El estado no existe."];
        }

        if (page < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (size is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        var (items, total) = await unitOfWork.ExecuteAsync(
            query.TenantId,
            ct => serviceRequests.ListForTechnicianAsync(query.TechnicianId, query.Status, (page - 1) * size, size, ct),
            cancellationToken);
        return new TechnicianServiceRequestPage(items, page, size, total);
    }
}