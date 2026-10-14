using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Support;

/// <summary>
/// Implementación en memoria de los puertos. Emula RLS: cada lectura solo ve las filas del tenant de la
/// transacción en curso, y fuera de una transacción no se ve nada (falla cerrado, como en el DD 10.2).
/// </summary>
public sealed class InMemoryStore :
    ITenantUnitOfWork, IServiceRequestRepository, ICategoryReplicaRepository, IProcessedEventRepository, IOutbox
{
    private Guid? currentTenant;

    public List<DomainServiceRequest> ServiceRequests { get; } = [];

    public List<CategoryReplica> Categories { get; } = [];

    public List<(Guid EventId, Guid TenantId, string EventType)> ProcessedEvents { get; } = [];

    public List<OutboxMessage> Outbox { get; } = [];

    public int Transactions { get; private set; }

    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Transactions++;
        currentTenant = tenantId;
        try
        {
            return await work(cancellationToken);
        }
        finally
        {
            currentTenant = null;
        }
    }

    public void Add(DomainServiceRequest serviceRequest) => ServiceRequests.Add(serviceRequest);

    public Task<DomainServiceRequest?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(ServiceRequests.SingleOrDefault(x => x.TenantId == currentTenant && x.Id == id));

    public Task<DomainServiceRequest?> FindForClientAsync(Guid id, Guid clientId, CancellationToken cancellationToken) =>
        Task.FromResult(ServiceRequests.SingleOrDefault(x => x.TenantId == currentTenant && x.Id == id && x.ClientId == clientId));

    public Task<DomainServiceRequest?> FindForTechnicianAsync(Guid id, Guid technicianId, CancellationToken cancellationToken) =>
        Task.FromResult(ServiceRequests.SingleOrDefault(x => x.TenantId == currentTenant && x.Id == id && x.TechnicianId == technicianId));

    public Task<bool> IsActiveAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(Categories.Any(x => x.TenantId == currentTenant && x.CategoryId == categoryId && x.Active));

    public Task<CategoryReplica?> FindAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(Categories.SingleOrDefault(x => x.TenantId == currentTenant && x.CategoryId == categoryId));

    public void Add(CategoryReplica category) => Categories.Add(category);

    public Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken) =>
        Task.FromResult(ProcessedEvents.Any(x => x.TenantId == currentTenant && x.EventId == eventId));

    public void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt) =>
        ProcessedEvents.Add((eventId, tenantId, eventType));

    public void Enqueue(OutboxMessage message) => Outbox.Add(message);
}

/// <summary>
/// Directorio de usuarios en memoria (en lugar de Identity): devuelve los usuarios de <see cref="Users"/> que se
/// pidan; con <see cref="Fail"/> simula que Identity no responde.
/// </summary>
public sealed class FakeUserDirectory : IUserDirectory
{
    public Dictionary<Guid, UserContact> Users { get; } = [];

    public bool Fail { get; set; }

    public List<(IReadOnlyCollection<Guid> Ids, string Token, Guid CorrelationId)> Calls { get; } = [];

    public Task<IReadOnlyDictionary<Guid, UserContact>> LookupAsync(
        IReadOnlyCollection<Guid> userIds, string accessToken, Guid correlationId, CancellationToken cancellationToken)
    {
        Calls.Add((userIds, accessToken, correlationId));
        IReadOnlyDictionary<Guid, UserContact> result = Fail
            ? new Dictionary<Guid, UserContact>()
            : userIds.Where(Users.ContainsKey).ToDictionary(id => id, id => Users[id]);
        return Task.FromResult(result);
    }
}

/// <summary>Reloj fijo para pruebas deterministas.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}