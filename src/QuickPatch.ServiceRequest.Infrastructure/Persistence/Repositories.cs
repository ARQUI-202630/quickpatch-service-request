using Microsoft.EntityFrameworkCore;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>
/// Transacción por tenant (DD, sección 10.2): fija <c>app.current_tenant</c> con <c>set_config(..., true)</c>,
/// equivalente a <c>SET LOCAL</c>, antes de ejecutar el trabajo; RLS filtra todo lo que se lea o escriba.
/// </summary>
public sealed class TenantUnitOfWork(ServiceRequestDbContext db) : ITenantUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("El tenant es obligatorio.", nameof(tenantId));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tenant = tenantId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT set_config('app.current_tenant', {tenant}, true)", cancellationToken);

        var result = await work(cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

public sealed class ServiceRequestRepository(ServiceRequestDbContext db) : IServiceRequestRepository
{
    public void Add(DomainServiceRequest serviceRequest) => db.ServiceRequests.Add(serviceRequest);

    public Task<DomainServiceRequest?> FindForClientAsync(Guid id, Guid clientId, CancellationToken cancellationToken) =>
        db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, cancellationToken);
}

public sealed class CategoryReplicaRepository(ServiceRequestDbContext db) : ICategoryReplicaRepository
{
    public Task<bool> IsActiveAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.Categories.AnyAsync(x => x.CategoryId == categoryId && x.Active, cancellationToken);

    public Task<CategoryReplica?> FindAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.Categories.SingleOrDefaultAsync(x => x.CategoryId == categoryId, cancellationToken);

    public void Add(CategoryReplica category) => db.Categories.Add(category);
}

public sealed class ProcessedEventRepository(ServiceRequestDbContext db) : IProcessedEventRepository
{
    public Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.ProcessedEvents.AnyAsync(x => x.EventId == eventId, cancellationToken);

    public void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt) =>
        db.ProcessedEvents.Add(new ProcessedEventRecord
        {
            EventId = eventId,
            TenantId = tenantId,
            EventType = eventType,
            ProcessedAt = processedAt,
        });
}

public sealed class EfOutbox(ServiceRequestDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.OutboxEvents.Add(new OutboxEventRecord
        {
            Id = message.Id,
            TenantId = message.TenantId,
            AggregateId = message.AggregateId,
            EventType = message.EventType,
            Payload = message.Payload,
            CreatedAt = clock.GetUtcNow(),
            Attempts = 0,
        });
    }
}