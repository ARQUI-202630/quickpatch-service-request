using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Panel operativo de solicitudes (contrato 1.4.0, RF-18, SCRUM-38; DD 8.3.15 y 10.4).</summary>
public class AdminPanelTests
{
    // 18 de octubre, 03:00 UTC = 17 de octubre, 22:00 en Bogotá: «hoy» empieza el 17 a las 05:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 3, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();

    private DomainServiceRequest Seed(Guid tenantId, string status, DateTimeOffset createdAt)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), Guid.NewGuid(), null, "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", status, createdAt, null);
        store.ServiceRequests.Add(request);
        return request;
    }

    [Theory]
    [InlineData("2026-10-18T03:00:00Z", "2026-10-17T05:00:00Z")]
    [InlineData("2026-10-18T05:00:00Z", "2026-10-18T05:00:00Z")]
    [InlineData("2026-10-18T04:59:59Z", "2026-10-17T05:00:00Z")]
    [InlineData("2026-01-01T02:00:00Z", "2025-12-31T05:00:00Z")]
    public void BogotaDay_EmpiezaALaMedianocheDeBogota(string now, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            BogotaDay.StartUtc(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Conteos_SiempreLosNueveEstadosEnOrden_YUnEstadoDesconocidoNoSePublica()
    {
        var counts = ServiceRequestCounts.From(
        [
            new StatusCountRow(ServiceRequestStatus.Asignado, 3, 1),
            new StatusCountRow("inventado", 2, 2),
        ]);

        Assert.Equal(ServiceRequestCounts.StatusOrder, counts.ByStatus.Keys);
        Assert.Equal(ServiceRequestStatus.All.Order(StringComparer.Ordinal), counts.ByStatus.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(3, counts.ByStatus[ServiceRequestStatus.Asignado]);
        Assert.Equal(0, counts.ByStatus[ServiceRequestStatus.Cancelado]);
        Assert.Equal(3, counts.CreatedToday);
    }

    [Fact]
    public async Task ConteosDelTenant_SoloSuTenant_YCreadasHoySegunBogota()
    {
        Seed(tenant, ServiceRequestStatus.BuscandoTecnico, Now.AddHours(-1));
        Seed(tenant, ServiceRequestStatus.BuscandoTecnico, new DateTimeOffset(2026, 10, 17, 4, 0, 0, TimeSpan.Zero));
        Seed(tenant, ServiceRequestStatus.Cancelado, Now.AddDays(-3));
        Seed(Guid.NewGuid(), ServiceRequestStatus.BuscandoTecnico, Now);

        var summary = await new GetTenantSummaryHandler(store, store, new FixedClock(Now)).HandleAsync(tenant, CancellationToken.None);

        Assert.Equal(Now, summary.GeneratedAt);
        Assert.Equal(2, summary.Counts.ByStatus[ServiceRequestStatus.BuscandoTecnico]);
        Assert.Equal(1, summary.Counts.ByStatus[ServiceRequestStatus.Cancelado]);
        Assert.Equal(1, summary.Counts.CreatedToday);
        Assert.Equal(1, store.Transactions);
    }

    [Fact]
    public async Task ListaDelTenant_SoloSuTenant_DeLaMasRecienteALaMasAntigua_ConFiltro()
    {
        var antigua = Seed(tenant, ServiceRequestStatus.Asignado, Now.AddHours(-5));
        var reciente = Seed(tenant, ServiceRequestStatus.Asignado, Now.AddHours(-1));
        Seed(tenant, ServiceRequestStatus.Pagado, Now.AddHours(-2));
        Seed(Guid.NewGuid(), ServiceRequestStatus.Asignado, Now);
        var handler = new ListTenantServiceRequestsHandler(store, store);

        var todas = await handler.HandleAsync(new ListTenantServiceRequestsQuery(tenant, null, null, null), CancellationToken.None);
        var asignadas = await handler.HandleAsync(
            new ListTenantServiceRequestsQuery(tenant, ServiceRequestStatus.Asignado, 2, 1), CancellationToken.None);

        Assert.Equal(3, todas.TotalItems);
        Assert.Equal(ListFilters.DefaultSize, todas.Size);
        Assert.Equal(reciente.Id, todas.Items[0].Id);
        Assert.Equal(2, asignadas.TotalItems);
        Assert.Equal(antigua.Id, Assert.Single(asignadas.Items).Id);
    }

    [Theory]
    [InlineData("inventado", null, null, "status")]
    [InlineData(null, 0, null, "page")]
    [InlineData(null, null, 101, "size")]
    public async Task ListaConFiltroInvalido_ErrorDelCampo_SinTransaccion(string? status, int? page, int? size, string field)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new ListTenantServiceRequestsHandler(store, store)
            .HandleAsync(new ListTenantServiceRequestsQuery(tenant, status, page, size), CancellationToken.None));

        Assert.Equal([field], ex.Errors.Keys);
        Assert.Equal(0, store.Transactions);
    }

    [Fact]
    public async Task Plataforma_UnaEntradaPorTenantConSolicitudes_YFiltroPorTenant()
    {
        var otro = Guid.NewGuid();
        Seed(tenant, ServiceRequestStatus.Asignado, Now);
        Seed(tenant, ServiceRequestStatus.EnEspera, Now.AddDays(-2));
        Seed(otro, ServiceRequestStatus.Asignado, Now);
        var handler = new GetPlatformSummaryHandler(store, new FixedClock(Now));

        var todos = await handler.HandleAsync(null, CancellationToken.None);
        var uno = await handler.HandleAsync(otro, CancellationToken.None);

        Assert.Equal(new[] { tenant, otro }.Order(), todos.Tenants.Select(t => t.TenantId));
        var propio = todos.Tenants.Single(t => t.TenantId == tenant);
        Assert.Equal(1, propio.Counts.ByStatus[ServiceRequestStatus.Asignado]);
        Assert.Equal(1, propio.Counts.ByStatus[ServiceRequestStatus.EnEspera]);
        Assert.Equal(1, propio.Counts.CreatedToday);
        Assert.Equal(otro, Assert.Single(uno.Tenants).TenantId);
        Assert.Empty((await handler.HandleAsync(Guid.NewGuid(), CancellationToken.None)).Tenants);
    }
}