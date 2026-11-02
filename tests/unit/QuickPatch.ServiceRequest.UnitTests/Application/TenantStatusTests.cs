using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Application.Tenants;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

public class ApplyTenantStatusChangedHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();

    private ApplyTenantStatusChangedHandler Handler() =>
        new(store, store, store, new FixedClock(T0), NullLogger<ApplyTenantStatusChangedHandler>.Instance);

    private EventEnvelope<TenantStatusChangedData> Event(string status, DateTimeOffset updatedAt, Guid? eventId = null, Guid? tenantId = null) =>
        new(eventId ?? Guid.NewGuid(), EventTypes.TenantStatusChanged, 1, updatedAt, Guid.NewGuid(), tenantId ?? tenant, "identity-service",
            new TenantStatusChangedData(tenantId ?? tenant, status, updatedAt));

    [Fact]
    public async Task Handle_TenantSinFila_CreaLaReplicaYRegistraElEvento()
    {
        var outcome = await Handler().HandleAsync(Event("inactivo", T0), CancellationToken.None);

        Assert.Equal(TenantStatusChangeOutcome.Applied, outcome);
        var replica = Assert.Single(store.TenantStatuses);
        Assert.Equal(tenant, replica.TenantId);
        Assert.True(replica.IsInactive);
        var processed = Assert.Single(store.ProcessedEvents);
        Assert.Equal(EventTypes.TenantStatusChanged, processed.EventType);
        Assert.Equal(tenant, processed.TenantId);
    }

    [Fact]
    public async Task Handle_MismoEventoDosVeces_SoloLoAplicaUnaVez()
    {
        var evento = Event("inactivo", T0);

        await Handler().HandleAsync(evento, CancellationToken.None);
        var outcome = await Handler().HandleAsync(evento, CancellationToken.None);

        Assert.Equal(TenantStatusChangeOutcome.Duplicate, outcome);
        Assert.Single(store.TenantStatuses);
        Assert.Single(store.ProcessedEvents);
    }

    [Fact]
    public async Task Handle_CambioMasReciente_Actualiza_YAtrasado_SeDescarta()
    {
        await Handler().HandleAsync(Event("inactivo", T0), CancellationToken.None);

        Assert.Equal(TenantStatusChangeOutcome.Applied, await Handler().HandleAsync(Event("activo", T0.AddMinutes(1)), CancellationToken.None));
        Assert.False(store.TenantStatuses.Single().IsInactive);
        Assert.Equal(TenantStatusChangeOutcome.Stale, await Handler().HandleAsync(Event("inactivo", T0.AddMinutes(-1)), CancellationToken.None));
        Assert.False(store.TenantStatuses.Single().IsInactive);
    }

    [Fact]
    public async Task Handle_CadaTenantTieneSuPropiaFila()
    {
        var otro = Guid.NewGuid();

        await Handler().HandleAsync(Event("inactivo", T0), CancellationToken.None);
        await Handler().HandleAsync(Event("activo", T0, tenantId: otro), CancellationToken.None);

        Assert.Equal(2, store.TenantStatuses.Count);
        Assert.True(store.TenantStatuses.Single(x => x.TenantId == tenant).IsInactive);
        Assert.False(store.TenantStatuses.Single(x => x.TenantId == otro).IsInactive);
    }

    [Fact]
    public async Task Handle_SinEvento_LanzaExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }
}

/// <summary>RN-T1 (SCRUM-348, SCRUM-510): un tenant desactivado no crea solicitudes aunque su token siga vigente.</summary>
public class CreateServiceRequestTenantStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid category = Guid.NewGuid();

    private CreateServiceRequestHandler Handler() =>
        new(store, store, store, store, store, new FakeGeocoder(), new FakeLocalityCatalog(), CoverageArea.Bogota, new FixedClock(Now));

    private CreateServiceRequestCommand Command(Guid? tenantId = null) =>
        new(tenantId ?? tenant, Guid.NewGuid(), Guid.NewGuid(), category, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45, apto 502");

    public CreateServiceRequestTenantStatusTests()
    {
        store.Categories.Add(new CategoryReplica(category, tenant, "Plomería", true, Now));
    }

    [Fact]
    public async Task TenantInactivo_RechazaSinGuardarNiPublicar()
    {
        store.TenantStatuses.Add(new TenantStatusReplica(tenant, TenantStatusReplica.Inactive, Now));

        var result = await Handler().HandleAsync(Command(), CancellationToken.None);

        Assert.IsType<CreateServiceRequestResult.TenantInactive>(result);
        Assert.Empty(store.ServiceRequests);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task TenantReactivado_VuelveACrear()
    {
        var replica = new TenantStatusReplica(tenant, TenantStatusReplica.Inactive, Now);
        store.TenantStatuses.Add(replica);
        Assert.IsType<CreateServiceRequestResult.TenantInactive>(await Handler().HandleAsync(Command(), CancellationToken.None));

        replica.ApplyChange(TenantStatusReplica.Active, Now.AddMinutes(1));
        var result = await Handler().HandleAsync(Command(), CancellationToken.None);

        Assert.IsType<CreateServiceRequestResult.Created>(result);
        Assert.Single(store.ServiceRequests);
    }

    [Fact]
    public async Task TenantSinFilaEnLaReplica_CuentaComoActivo()
    {
        var result = await Handler().HandleAsync(Command(), CancellationToken.None);

        Assert.IsType<CreateServiceRequestResult.Created>(result);
    }

    [Fact]
    public async Task OtroTenantInactivo_NoAfectaAEste()
    {
        store.TenantStatuses.Add(new TenantStatusReplica(Guid.NewGuid(), TenantStatusReplica.Inactive, Now));

        var result = await Handler().HandleAsync(Command(), CancellationToken.None);

        Assert.IsType<CreateServiceRequestResult.Created>(result);
    }
}