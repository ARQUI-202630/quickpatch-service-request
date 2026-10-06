using QuickPatch.ServiceRequest.Domain.Categories;

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

    /// <summary>Busca la solicitud del cliente; devuelve null si no existe o es de otro cliente.</summary>
    Task<DomainServiceRequest?> FindForClientAsync(Guid id, Guid clientId, CancellationToken cancellationToken);
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