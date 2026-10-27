using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
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

/// <summary>Persistencia de cotizaciones (tabla <c>quotes</c>, DD 5.11), siempre bajo el tenant de la transacción.</summary>
public interface IQuoteRepository
{
    void Add(Quote quote);

    /// <summary>
    /// Cotizaciones de la solicitud, de la más antigua a la más reciente. Las que se vayan a modificar se leen dentro de la
    /// transacción que ya bloqueó la fila de la solicitud (<see cref="IServiceRequestRepository.FindByIdAsync"/>): ese
    /// bloqueo serializa emitir, responder, vencer y cancelar (RN-Q7).
    /// </summary>
    Task<IReadOnlyList<Quote>> ListAsync(Guid serviceRequestId, CancellationToken cancellationToken);
}

/// <summary>Persistencia de evidencias (tabla <c>service_evidence</c>, DD 5.10), siempre bajo el tenant de la transacción.</summary>
public interface IEvidenceRepository
{
    void Add(ServiceEvidence evidence);

    /// <summary>
    /// Cuántas evidencias tiene la solicitud. Completar la cuenta en la transacción que ya bloqueó la fila de la solicitud
    /// (RN-SR3): así no se cruza con una subida.
    /// </summary>
    Task<int> CountAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    /// <summary>Evidencias de la solicitud, de la más antigua a la más reciente.</summary>
    Task<IReadOnlyList<ServiceEvidence>> ListAsync(Guid serviceRequestId, CancellationToken cancellationToken);
}

/// <summary>Persistencia de calificaciones (tabla <c>ratings</c>, DD 5.9), siempre bajo el tenant de la transacción.</summary>
public interface IRatingRepository
{
    void Add(Rating rating);

    /// <summary>
    /// Si la solicitud ya tiene calificación (RN-R2). Se consulta en la transacción que ya bloqueó la fila de la solicitud;
    /// el índice único sobre <c>service_request_id</c> respalda la regla en la base.
    /// </summary>
    Task<bool> ExistsAsync(Guid serviceRequestId, CancellationToken cancellationToken);
}

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

/// <summary>
/// Cotizaciones pendientes ya vencidas de todos los tenants (RN-Q6). Usa el rol <c>service_request_scheduler</c> (P-06,
/// DD 10.2), que solo lee identificadores, tenant, estado y vencimiento; cada cotización se vence después en su tenant.
/// </summary>
public interface IExpiredQuoteFinder
{
    Task<IReadOnlyList<ExpiredQuoteRef>> FindAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

public sealed record ExpiredQuoteRef(Guid QuoteId, Guid TenantId, Guid ServiceRequestId);

/// <summary>
/// Candado distribuido para las tareas programadas (coordinación temporal en Redis, DD 7.13 para RN-Q6 y SAD 5.1): con
/// varias réplicas, solo una recorre las cotizaciones vencidas en cada vuelta.
/// </summary>
public interface IDistributedLock
{
    /// <summary>Toma el candado por <paramref name="ttl"/>; null si lo tiene otra réplica. Se suelta al desecharlo.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken);
}

/// <summary>Réplica local de categorías (tabla <c>service_request_categories</c>).</summary>
public interface ICategoryReplicaRepository
{
    Task<bool> IsActiveAsync(Guid categoryId, CancellationToken cancellationToken);

    Task<CategoryReplica?> FindAsync(Guid categoryId, CancellationToken cancellationToken);

    /// <summary>Categorías activas del tenant de la transacción, ordenadas por nombre (contrato 1.8.0, ADR-025).</summary>
    Task<IReadOnlyList<CategoryReplica>> ListActiveAsync(CancellationToken cancellationToken);

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

/// <summary>
/// Auditoría de ServiceRequest (tabla <c>audit_logs</c> de <c>db_service_request</c>, DD 5.14, v5.3): la fila se guarda en
/// la misma transacción que la operación. Solo se insertan filas (RN-A1).
/// </summary>
public interface IAuditLog
{
    void Add(AuditEntry entry);
}

/// <summary>Fila de <c>audit_logs</c>. <see cref="Metadata"/> es JSON y nunca lleva datos personales del usuario.</summary>
public sealed record AuditEntry(Guid Id, Guid TenantId, Guid ActorUserId, string Action, Guid TargetId, string Metadata, DateTimeOffset CreatedAt);

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

    /// <summary>Las 20 localidades, ordenadas por código (contrato 1.8.0, ADR-025).</summary>
    Task<IReadOnlyList<Locality>> ListAsync(CancellationToken cancellationToken);
}