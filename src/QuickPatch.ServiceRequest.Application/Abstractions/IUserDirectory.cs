using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>
/// Nombre y teléfono de usuarios, leídos de Identity con la consulta interna por lote
/// (<c>identity-internal.v1.yaml</c>, DD V4 8.3.4). ServiceRequest no guarda esos datos.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// Devuelve los usuarios encontrados en el tenant del token. Si Identity no responde a tiempo o falla,
    /// devuelve un diccionario vacío: el detalle se entrega igual, con los nombres en null.
    /// </summary>
    /// <param name="accessToken">JWT del usuario de la petición; se reenvía sin cambios y nunca se registra.</param>
    Task<IReadOnlyDictionary<Guid, UserContact>> LookupAsync(
        IReadOnlyCollection<Guid> userIds, string accessToken, Guid correlationId, CancellationToken cancellationToken);
}

public sealed record UserContact(Guid Id, string FullName, string? Phone);