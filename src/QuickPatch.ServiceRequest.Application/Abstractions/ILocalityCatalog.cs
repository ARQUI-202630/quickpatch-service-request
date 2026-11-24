using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Copia de solo lectura de las localidades de Bogotá (tabla <c>localities</c>, P-09).</summary>
public interface ILocalityCatalog
{
    /// <summary>Localidad que contiene el punto; null si el punto no cae en ninguna de las 20.</summary>
    Task<Locality?> FindContainingAsync(GeoPoint point, CancellationToken cancellationToken);

    /// <summary>Las 20 localidades, ordenadas por código (contrato 1.8.0, ADR-025).</summary>
    Task<IReadOnlyList<Locality>> ListAsync(CancellationToken cancellationToken);
}