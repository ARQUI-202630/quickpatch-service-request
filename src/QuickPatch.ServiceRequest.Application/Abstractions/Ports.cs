using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>
/// Ejecuta un trabajo dentro de una transacción que primero fija el tenant de la sesión
/// (<c>SET LOCAL app.current_tenant</c>, DD sección 10.2), para que RLS aísle los datos.
/// Al terminar el trabajo guarda los cambios y confirma la transacción.
/// </summary>
public interface ITenantUnitOfWork
{
    Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>Persistencia de solicitudes (tabla <c>service_requests</c>).</summary>
public interface IServiceRequestRepository
{
    void Add(DomainServiceRequest serviceRequest);

    /// <summary>Busca la solicitud en el tenant de la transacción para modificarla; null si no existe.</summary>
    Task<DomainServiceRequest?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Busca la solicitud del cliente; devuelve null si no existe o es de otro cliente.</summary>
    Task<DomainServiceRequest?> FindForClientAsync(Guid id, Guid clientId, CancellationToken cancellationToken);

    /// <summary>Busca la solicitud asignada a ese técnico; devuelve null si no existe o tiene otro técnico.</summary>
    Task<DomainServiceRequest?> FindForTechnicianAsync(Guid id, Guid technicianId, CancellationToken cancellationToken);

    /// <summary>
    /// Página de las solicitudes asignadas al técnico en el tenant de la transacción, de la asignación más reciente a la
    /// más antigua, y el total que cumple el filtro.
    /// </summary>
    Task<(IReadOnlyList<DomainServiceRequest> Items, int Total)> ListForTechnicianAsync(
        Guid technicianId, string? status, int skip, int take, CancellationToken cancellationToken);
}

/// <summary>Réplica local de categorías (tabla <c>service_request_categories</c>).</summary>
public interface ICategoryReplicaRepository
{
    Task<bool> IsActiveAsync(Guid categoryId, CancellationToken cancellationToken);

    Task<CategoryReplica?> FindAsync(Guid categoryId, CancellationToken cancellationToken);

    void Add(CategoryReplica category);
}

/// <summary>Registro de eventos ya aplicados (tabla <c>processed_events</c>, RN-EV1).</summary>
public interface IProcessedEventRepository
{
    Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken);

    void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt);
}

/// <summary>Transactional Outbox (ADR-007): el evento se guarda en la misma transacción que el cambio.</summary>
public interface IOutbox
{
    void Enqueue(OutboxMessage message);
}

/// <summary>Mensaje pendiente de publicar en Kafka. <see cref="Id"/> se publica como <c>eventId</c>.</summary>
public sealed record OutboxMessage(Guid Id, Guid TenantId, Guid AggregateId, string EventType, string Payload);

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

/// <summary>
/// Geocodificador de direcciones dentro de Bogotá: el Nominatim propio de la VM de datos (ADR-024, DD 8.3.3).
/// </summary>
public interface IGeocoder
{
    /// <summary>
    /// Devuelve el punto y lo que reconoció el geocodificador, o null si no encontró la dirección, no está configurado
    /// o no respondió a tiempo. La dirección es un dato personal: nunca se registra en logs (K12).
    /// </summary>
    Task<GeocodedAddress?> GeocodeAsync(string addressText, CancellationToken cancellationToken);
}

public sealed record GeocodedAddress(GeoPoint Location, string MatchedAddress);

/// <summary>Copia de solo lectura de las localidades de Bogotá (tabla <c>localities</c>, P-09).</summary>
public interface ILocalityCatalog
{
    /// <summary>Localidad que contiene el punto; null si el punto no cae en ninguna de las 20.</summary>
    Task<Locality?> FindContainingAsync(GeoPoint point, CancellationToken cancellationToken);
}