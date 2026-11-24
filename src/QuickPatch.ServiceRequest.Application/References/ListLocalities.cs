using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

namespace QuickPatch.ServiceRequest.Application.References;

/// <summary>Las 20 localidades de Bogotá desde la copia de ServiceRequest (P-09; contrato 1.8.0, ADR-025).</summary>
public sealed class ListLocalitiesHandler(ILocalityCatalog localities)
{
    public Task<IReadOnlyList<Locality>> HandleAsync(CancellationToken cancellationToken) => localities.ListAsync(cancellationToken);
}