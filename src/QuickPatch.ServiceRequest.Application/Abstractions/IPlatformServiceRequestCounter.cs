using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Conteo de un estado en un tenant (vista de plataforma).</summary>
public sealed record TenantStatusCountRow(Guid TenantId, string Status, int Total, int CreatedSince);

/// <summary>
/// Conteos de solicitudes de todos los tenants para el <c>admin_plataforma</c> (DD 10.4, DEP-15). Es la única lectura de
/// ServiceRequest fuera de RLS: usa el rol <c>service_request_platform</c>, que solo ve <c>tenant_id</c>, <c>status</c> y
/// <c>created_at</c>, en una transacción de solo lectura. Nunca devuelve el contenido de una solicitud.
/// </summary>
public interface IPlatformServiceRequestCounter
{
    /// <param name="tenantId">Solo ese tenant; null para todos.</param>
    Task<IReadOnlyList<TenantStatusCountRow>> CountByTenantAsync(
        Guid? tenantId, DateTimeOffset createdSince, CancellationToken cancellationToken);
}