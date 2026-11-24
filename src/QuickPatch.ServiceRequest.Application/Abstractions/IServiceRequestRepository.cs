using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Persistencia de solicitudes (tabla <c>service_requests</c>).</summary>
public interface IServiceRequestRepository
{
    void Add(DomainServiceRequest serviceRequest);

    /// <summary>
    /// Busca la solicitud en el tenant de la transacción para modificarla y bloquea su fila hasta el final de la
    /// transacción (<c>SELECT ... FOR UPDATE</c>): la cancelación y los eventos de Matching no se pisan (DD 8.3.9). Null si
    /// no existe.
    /// </summary>
    Task<DomainServiceRequest?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Busca la solicitud en el tenant de la transacción, sin bloquearla (lecturas del <c>admin_tenant</c>).</summary>
    Task<DomainServiceRequest?> FindInTenantAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Busca la solicitud del cliente; devuelve null si no existe o es de otro cliente.</summary>
    Task<DomainServiceRequest?> FindForClientAsync(Guid id, Guid clientId, CancellationToken cancellationToken);

    /// <summary>
    /// Busca la solicitud que <paramref name="userId"/> creó o, si trae empresa, una de su empresa (RN-SR14); null si no
    /// existe o no la puede ver.
    /// </summary>
    Task<DomainServiceRequest?> FindForCompanyMemberAsync(Guid id, Guid userId, Guid companyId, CancellationToken cancellationToken);

    /// <summary>Busca la solicitud asignada a ese técnico; devuelve null si no existe o tiene otro técnico.</summary>
    Task<DomainServiceRequest?> FindForTechnicianAsync(Guid id, Guid technicianId, CancellationToken cancellationToken);

    /// <summary>
    /// Página de las solicitudes asignadas al técnico en el tenant de la transacción, de la asignación más reciente a la
    /// más antigua, y el total que cumple el filtro.
    /// </summary>
    Task<(IReadOnlyList<DomainServiceRequest> Items, int Total)> ListForTechnicianAsync(
        Guid technicianId, string? status, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Página de las solicitudes del tenant de la transacción, de la más reciente a la más antigua, y el total que cumple
    /// el filtro. Sin <paramref name="scope"/> son todas (panel del <c>admin_tenant</c>, contrato 1.4.0); con él, las del
    /// cliente o las de la empresa (contrato 1.9.0).
    /// </summary>
    Task<(IReadOnlyList<DomainServiceRequest> Items, int Total)> ListForTenantAsync(
        string? status, ListScope? scope, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Cuántas solicitudes del tenant de la transacción hay por estado y cuántas se crearon desde <paramref name="createdSince"/>.</summary>
    Task<IReadOnlyList<StatusCountRow>> CountByStatusAsync(DateTimeOffset createdSince, CancellationToken cancellationToken);
}

/// <summary>
/// Qué solicitudes lista quien no es administrador (contrato 1.9.0): las que creó <see cref="ClientId"/> o, con
/// <see cref="CompanyId"/>, las de su empresa (RN-SR14).
/// </summary>
public sealed record ListScope(Guid ClientId, Guid? CompanyId);

/// <summary>Conteo de un estado: total y creadas desde el inicio del día.</summary>
public sealed record StatusCountRow(string Status, int Total, int CreatedSince);