using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Application.Categories;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

public class CreateServiceRequestHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();
    private readonly Guid category = Guid.NewGuid();
    private readonly Guid correlation = Guid.NewGuid();

    private CreateServiceRequestHandler Handler() =>
        new(store, store, store, store, CoverageArea.Bogota, new FixedClock(Now));

    private CreateServiceRequestCommand Command(GeoPoint? location = null, Guid? categoryId = null) =>
        new(tenant, client, correlation, categoryId ?? category, "Fuga de agua en la cocina", location ?? new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45, apto 502");

    [Fact]
    public async Task Handle_DatosValidos_GuardaLaSolicitudYElEventoEnLaMismaTransaccion()
    {
        store.Categories.Add(new CategoryReplica(category, tenant, "Plomería", true, Now));

        var result = await Handler().HandleAsync(Command(), CancellationToken.None);

        var created = Assert.IsType<CreateServiceRequestResult.Created>(result);
        Assert.Single(store.ServiceRequests);
        Assert.Equal(1, store.Transactions);
        var message = Assert.Single(store.Outbox);
        Assert.Equal(EventTypes.ServiceRequestCreated, message.EventType);
        Assert.Equal(created.ServiceRequest.Id, message.AggregateId);
        Assert.Equal(tenant, message.TenantId);

        using var json = JsonDocument.Parse(message.Payload);
        var root = json.RootElement;
        Assert.Equal(message.Id, root.GetProperty("eventId").GetGuid());
        Assert.Equal("service-request.created", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("eventVersion").GetInt32());
        Assert.Equal(correlation, root.GetProperty("correlationId").GetGuid());
        Assert.Equal(tenant, root.GetProperty("tenantId").GetGuid());
        Assert.Equal("service-request-service", root.GetProperty("producer").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(created.ServiceRequest.Id, data.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal(client, data.GetProperty("clientId").GetGuid());
        Assert.Equal("Fuga de agua en la cocina", data.GetProperty("description").GetString());
        Assert.Equal(4.6486, data.GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.False(data.TryGetProperty("addressText", out _), "addressText no viaja en el evento (K12).");
    }

    [Fact]
    public async Task Handle_FueraDeBogota_NoAbreTransaccionNiGuarda()
    {
        store.Categories.Add(new CategoryReplica(category, tenant, "Plomería", true, Now));

        var result = await Handler().HandleAsync(Command(new GeoPoint(6.2442, -75.5812)), CancellationToken.None);

        Assert.IsType<CreateServiceRequestResult.OutOfCoverage>(result);
        Assert.Equal(0, store.Transactions);
        Assert.Empty(store.ServiceRequests);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task Handle_CategoriaInactiva_NoGuarda()
    {
        store.Categories.Add(new CategoryReplica(category, tenant, "Plomería", false, Now));

        var result = await Handler().HandleAsync(Command(), CancellationToken.None);

        Assert.IsType<CreateServiceRequestResult.CategoryUnavailable>(result);
        Assert.Empty(store.ServiceRequests);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task Handle_CategoriaDeOtroTenant_NoEstaDisponible()
    {
        store.Categories.Add(new CategoryReplica(category, Guid.NewGuid(), "Plomería", true, Now));

        var result = await Handler().HandleAsync(Command(), CancellationToken.None);

        Assert.IsType<CreateServiceRequestResult.CategoryUnavailable>(result);
    }

    [Fact]
    public async Task Handle_DatosInvalidos_LanzaValidacion()
    {
        var command = Command() with { Description = "corta" };

        await Assert.ThrowsAsync<DomainValidationException>(() => Handler().HandleAsync(command, CancellationToken.None));
        Assert.Equal(0, store.Transactions);
    }

    [Fact]
    public async Task Handle_SinComando_LanzaExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Get_SoloDevuelveLaSolicitudDelMismoTenantYCliente()
    {
        store.Categories.Add(new CategoryReplica(category, tenant, "Plomería", true, Now));
        var created = (CreateServiceRequestResult.Created)await Handler().HandleAsync(Command(), CancellationToken.None);
        var get = new GetServiceRequestHandler(store, store);
        var id = created.ServiceRequest.Id;

        Assert.NotNull(await get.HandleAsync(tenant, client, id, CancellationToken.None));
        Assert.Null(await get.HandleAsync(Guid.NewGuid(), client, id, CancellationToken.None));
        Assert.Null(await get.HandleAsync(tenant, Guid.NewGuid(), id, CancellationToken.None));
    }
}

public class ApplyCategoryChangedHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid category = Guid.NewGuid();

    private ApplyCategoryChangedHandler Handler() =>
        new(store, store, store, new FixedClock(T0), NullLogger<ApplyCategoryChangedHandler>.Instance);

    private EventEnvelope<CategoryChangedData> Event(bool active, DateTimeOffset updatedAt, Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), EventTypes.CategoryChanged, 1, updatedAt, Guid.NewGuid(), tenant, "catalog-service",
            new CategoryChangedData(category, "Plomería", active, updatedAt));

    [Fact]
    public async Task Handle_CategoriaNueva_CreaLaReplicaYRegistraElEvento()
    {
        var outcome = await Handler().HandleAsync(Event(true, T0), CancellationToken.None);

        Assert.Equal(CategoryChangeOutcome.Applied, outcome);
        var replica = Assert.Single(store.Categories);
        Assert.Equal(tenant, replica.TenantId);
        Assert.Single(store.ProcessedEvents);
    }

    [Fact]
    public async Task Handle_MismoEventoDosVeces_SoloLoAplicaUnaVez()
    {
        var evento = Event(true, T0);

        await Handler().HandleAsync(evento, CancellationToken.None);
        var outcome = await Handler().HandleAsync(evento, CancellationToken.None);

        Assert.Equal(CategoryChangeOutcome.Duplicate, outcome);
        Assert.Single(store.Categories);
        Assert.Single(store.ProcessedEvents);
    }

    [Fact]
    public async Task Handle_CambioMasReciente_Actualiza_YAtrasado_SeDescarta()
    {
        await Handler().HandleAsync(Event(true, T0), CancellationToken.None);

        Assert.Equal(CategoryChangeOutcome.Applied, await Handler().HandleAsync(Event(false, T0.AddMinutes(1)), CancellationToken.None));
        Assert.False(store.Categories.Single().Active);
        Assert.Equal(CategoryChangeOutcome.Stale, await Handler().HandleAsync(Event(true, T0.AddMinutes(-1)), CancellationToken.None));
        Assert.False(store.Categories.Single().Active);
    }

    [Fact]
    public async Task Handle_SinEvento_LanzaExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }
}