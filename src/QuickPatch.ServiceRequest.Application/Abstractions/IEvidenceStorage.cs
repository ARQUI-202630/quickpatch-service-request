using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>
/// Almacenamiento de los archivos de evidencia: el bucket <c>service-request-evidencias</c> de Garage (RN-E3, ADR-016), con
/// la llave <c>service-request</c>, que solo ve ese bucket.
/// </summary>
public interface IEvidenceStorage
{
    Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);

    /// <summary>El archivo y su tipo; null si el objeto no existe.</summary>
    Task<StoredEvidence?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Borra el objeto; no falla si no existe.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Archivo leído del almacenamiento. Quien lo recibe debe desechar <see cref="Content"/>.</summary>
public sealed record StoredEvidence(Stream Content, string ContentType, long? Length);