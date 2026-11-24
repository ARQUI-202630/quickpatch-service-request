using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Réplica local de categorías (tabla <c>service_request_categories</c>).</summary>
public interface ICategoryReplicaRepository
{
    Task<bool> IsActiveAsync(Guid categoryId, CancellationToken cancellationToken);

    Task<CategoryReplica?> FindAsync(Guid categoryId, CancellationToken cancellationToken);

    /// <summary>Categorías activas del tenant de la transacción, ordenadas por nombre (contrato 1.8.0, ADR-025).</summary>
    Task<IReadOnlyList<CategoryReplica>> ListActiveAsync(CancellationToken cancellationToken);

    void Add(CategoryReplica category);
}