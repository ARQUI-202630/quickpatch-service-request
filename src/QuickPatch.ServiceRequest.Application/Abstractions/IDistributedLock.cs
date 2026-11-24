using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>
/// Candado distribuido para las tareas programadas (coordinación temporal en Redis, DD 7.13 para RN-Q6 y SAD 5.1): con
/// varias réplicas, solo una recorre las cotizaciones vencidas en cada vuelta.
/// </summary>
public interface IDistributedLock
{
    /// <summary>Toma el candado por <paramref name="ttl"/>; null si lo tiene otra réplica. Se suelta al desecharlo.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken);
}