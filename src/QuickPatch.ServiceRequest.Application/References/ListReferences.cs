using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

namespace QuickPatch.ServiceRequest.Application.References;

/// <summary>
/// Categorías activas de un tenant desde la réplica de <c>catalog.category-changed</c> (contrato 1.8.0, ADR-025): el gateway
/// solo tiene REST hacia Identity, ServiceRequest y Payments, así que las apps ya no le piden la lista a Catalog. El tenant
/// sale del token o del canal; nunca de la petición.
/// </summary>
public sealed class ListCategoriesHandler(ITenantUnitOfWork unitOfWork, ICategoryReplicaRepository categories)
{
    public Task<IReadOnlyList<CategoryReplica>> HandleAsync(Guid tenantId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(tenantId, categories.ListActiveAsync, cancellationToken);
}

/// <summary>Las 20 localidades de Bogotá desde la copia de ServiceRequest (P-09; contrato 1.8.0, ADR-025).</summary>
public sealed class ListLocalitiesHandler(ILocalityCatalog localities)
{
    public Task<IReadOnlyList<Locality>> HandleAsync(CancellationToken cancellationToken) => localities.ListAsync(cancellationToken);
}