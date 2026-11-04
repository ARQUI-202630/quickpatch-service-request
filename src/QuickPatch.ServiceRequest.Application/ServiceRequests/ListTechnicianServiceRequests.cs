using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>Lista del técnico (<c>GET /v1/technicians/me/service-requests</c>, contrato 1.10.0). Todo sale del JWT salvo los filtros.</summary>
public sealed record ListTechnicianServiceRequestsQuery(Guid TenantId, Guid TechnicianId, string? Status, int? Page, int? Size);

/// <summary>Página de la lista; <paramref name="Ratings"/> trae la calificación de las solicitudes de la página que la tienen.</summary>
public sealed record TechnicianServiceRequestPage(
    IReadOnlyList<DomainServiceRequest> Items, IReadOnlyDictionary<Guid, Rating> Ratings, int Page, int Size, int TotalItems);

/// <summary>
/// Solicitudes asignadas al técnico del token, de la asignación más reciente a la más antigua (RF-17, SCRUM-464). Corre
/// bajo el tenant del token (RLS). La API entrega solo los campos sin contacto (RN-SR13); el detalle sigue en
/// <see cref="GetServiceRequestHandler"/>. Desde 1.10.0 cada solicitud trae su calificación si la tiene, que es el
/// historial del técnico (RF-17, SCRUM-100): solo el puntaje y la fecha, nunca el comentario.
/// </summary>
public sealed class ListTechnicianServiceRequestsHandler(
    ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, IRatingRepository ratings)
{
    public const int DefaultSize = ListFilters.DefaultSize;
    public const int MaxSize = ListFilters.MaxSize;

    public async Task<TechnicianServiceRequestPage> HandleAsync(ListTechnicianServiceRequestsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (page, size) = ListFilters.Validate(query.Status, query.Page, query.Size);

        var (items, rated, total) = await unitOfWork.ExecuteAsync(
            query.TenantId,
            async ct =>
            {
                var (items, total) = await serviceRequests.ListForTechnicianAsync(query.TechnicianId, query.Status, (page - 1) * size, size, ct);
                var rated = await ratings.FindByServiceRequestsAsync(items.Select(x => x.Id).ToList(), ct);
                return (items, rated, total);
            },
            cancellationToken);
        return new TechnicianServiceRequestPage(items, rated, page, size, total);
    }
}