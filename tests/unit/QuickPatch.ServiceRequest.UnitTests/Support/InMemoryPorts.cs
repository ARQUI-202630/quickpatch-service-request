using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Support;

/// <summary>
/// Implementación en memoria de los puertos. Emula RLS: cada lectura solo ve las filas del tenant de la
/// transacción en curso, y fuera de una transacción no se ve nada (falla cerrado, como en el DD 10.2). El conteo de
/// plataforma es la excepción, como el rol <c>service_request_platform</c>: ve todos los tenants, pero solo cuenta; y la
/// búsqueda de cotizaciones vencidas, como <c>service_request_scheduler</c>: ve todos los tenants, pero solo referencias.
/// </summary>
public sealed class InMemoryStore :
    ITenantUnitOfWork, IServiceRequestRepository, ICategoryReplicaRepository, IProcessedEventRepository, IOutbox,
    IPlatformServiceRequestCounter, IAuditLog, IQuoteRepository, IExpiredQuoteFinder
{
    public List<AuditEntry> Audit { get; } = [];

    public void Add(AuditEntry entry) => Audit.Add(entry);

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

    public Task<(IReadOnlyList<DomainServiceRequest> Items, int Total)> ListForTechnicianAsync(
        Guid technicianId, string? status, int skip, int take, CancellationToken cancellationToken)
    {
        List<DomainServiceRequest> matches;
        lock (this)
        {
            matches = ServiceRequests
                .Where(x => x.TenantId == currentTenant && x.TechnicianId == technicianId && (status is null || x.Status == status))
                .OrderByDescending(x => x.AssignedAt)
                .ThenByDescending(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToList();
        }

        return Task.FromResult<(IReadOnlyList<DomainServiceRequest>, int)>((matches.Skip(skip).Take(take).ToList(), matches.Count));
    }

    public Task<(IReadOnlyList<DomainServiceRequest> Items, int Total)> ListForTenantAsync(
        string? status, int skip, int take, CancellationToken cancellationToken)
    {
        List<DomainServiceRequest> matches;
        lock (this)
        {
            matches = ServiceRequests
                .Where(x => x.TenantId == currentTenant && (status is null || x.Status == status))
                .OrderByDescending(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToList();
        }

        return Task.FromResult<(IReadOnlyList<DomainServiceRequest>, int)>((matches.Skip(skip).Take(take).ToList(), matches.Count));
    }

    public Task<IReadOnlyList<StatusCountRow>> CountByStatusAsync(DateTimeOffset createdSince, CancellationToken cancellationToken)
    {
        lock (this)
        {
            IReadOnlyList<StatusCountRow> rows = ServiceRequests
                .Where(x => x.TenantId == currentTenant)
                .GroupBy(x => x.Status)
                .Select(g => new StatusCountRow(g.Key, g.Count(), g.Count(x => x.CreatedAt >= createdSince)))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<TenantStatusCountRow>> CountByTenantAsync(
        Guid? tenantId, DateTimeOffset createdSince, CancellationToken cancellationToken)
    {
        lock (this)
        {
            IReadOnlyList<TenantStatusCountRow> rows = ServiceRequests
                .Where(x => tenantId is null || x.TenantId == tenantId)
                .GroupBy(x => (x.TenantId, x.Status))
                .Select(g => new TenantStatusCountRow(g.Key.TenantId, g.Key.Status, g.Count(), g.Count(x => x.CreatedAt >= createdSince)))
                .ToList();
            return Task.FromResult(rows);
        }
    }

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

    public List<Quote> Quotes { get; } = [];

    public void Add(Quote quote) => Quotes.Add(quote);

    public Task<IReadOnlyList<Quote>> ListAsync(Guid serviceRequestId, CancellationToken cancellationToken)
    {
        lock (this)
        {
            IReadOnlyList<Quote> rows = Quotes
                .Where(x => x.TenantId == currentTenant && x.ServiceRequestId == serviceRequestId)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<ExpiredQuoteRef>> FindAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        lock (this)
        {
            IReadOnlyList<ExpiredQuoteRef> rows = Quotes
                .Where(x => x.Status == QuoteStatus.Pendiente && x.ExpiresAt <= now)
                .OrderBy(x => x.ExpiresAt)
                .Take(limit)
                .Select(x => new ExpiredQuoteRef(x.Id, x.TenantId, x.ServiceRequestId))
                .ToList();
            return Task.FromResult(rows);
        }
    }
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

/// <summary>Geocodificador falso: devuelve <see cref="Result"/> y guarda las direcciones que recibió.</summary>
public sealed class FakeGeocoder : IGeocoder
{
    public GeocodedAddress? Result { get; set; } = new(new GeoPoint(4.6486, -74.063), "Calle 63, Chapinero, Bogotá");

    public List<string> Calls { get; } = [];

    public Task<GeocodedAddress?> GeocodeAsync(string addressText, CancellationToken cancellationToken)
    {
        Calls.Add(addressText);
        return Task.FromResult(Result);
    }
}

/// <summary>Catálogo de localidades falso: Chapinero para cualquier punto, salvo que <see cref="Result"/> sea null.</summary>
public sealed class FakeLocalityCatalog : ILocalityCatalog
{
    public Locality? Result { get; set; } = new("02", "Chapinero");

    public Task<Locality?> FindContainingAsync(GeoPoint point, CancellationToken cancellationToken) => Task.FromResult(Result);
}

/// <summary>Reloj fijo para pruebas deterministas.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}