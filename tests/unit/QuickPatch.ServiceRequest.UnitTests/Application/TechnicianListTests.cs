using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Lista de solicitudes del técnico (contrato 1.2.0, RF-17, SCRUM-464).</summary>
public class TechnicianListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();

    private DomainServiceRequest Seed(Guid tenantId, Guid? technicianId, string status, int assignedMinute)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), Guid.NewGuid(), technicianId, "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", status, Now, technicianId is null ? null : Now.AddMinutes(assignedMinute));
        store.ServiceRequests.Add(request);
        return request;
    }

    private ListTechnicianServiceRequestsHandler Handler() => new(store, store);

    private Task<TechnicianServiceRequestPage> List(string? status = null, int? page = null, int? size = null) =>
        Handler().HandleAsync(new ListTechnicianServiceRequestsQuery(tenant, technician, status, page, size), CancellationToken.None);

    [Fact]
    public async Task SoloLasDelTecnicoYDelTenant_DeLaMasRecienteALaMasAntigua()
    {
        var antigua = Seed(tenant, technician, ServiceRequestStatus.Completado, 1);
        var reciente = Seed(tenant, technician, ServiceRequestStatus.Asignado, 30);
        Seed(tenant, Guid.NewGuid(), ServiceRequestStatus.Asignado, 40);
        Seed(tenant, null, ServiceRequestStatus.BuscandoTecnico, 0);
        Seed(Guid.NewGuid(), technician, ServiceRequestStatus.Asignado, 50);

        var result = await List();

        Assert.Equal([reciente.Id, antigua.Id], result.Items.Select(x => x.Id));
        Assert.Equal(2, result.TotalItems);
        Assert.Equal(1, result.Page);
        Assert.Equal(ListTechnicianServiceRequestsHandler.DefaultSize, result.Size);
        Assert.Equal(1, store.Transactions);
    }

    [Fact]
    public async Task FiltraPorEstado_YPagina()
    {
        for (var i = 0; i < 5; i++)
        {
            Seed(tenant, technician, ServiceRequestStatus.Asignado, i);
        }

        Seed(tenant, technician, ServiceRequestStatus.Pagado, 10);

        var segunda = await List(ServiceRequestStatus.Asignado, page: 2, size: 2);
        var pagadas = await List(ServiceRequestStatus.Pagado);

        Assert.Equal(5, segunda.TotalItems);
        Assert.Equal(2, segunda.Items.Count);
        Assert.All(segunda.Items, x => Assert.Equal(ServiceRequestStatus.Asignado, x.Status));
        Assert.Equal([Now.AddMinutes(2), Now.AddMinutes(1)], segunda.Items.Select(x => x.AssignedAt!.Value));
        Assert.Single(pagadas.Items);
    }

    [Theory]
    [InlineData("inventado", null, null, "status")]
    [InlineData(null, 0, null, "page")]
    [InlineData(null, null, 0, "size")]
    [InlineData(null, null, 101, "size")]
    public async Task FiltrosInvalidos_ErrorDeValidacionConElCampo(string? status, int? page, int? size, string campo)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => List(status, page, size));

        Assert.True(ex.Errors.ContainsKey(campo));
        Assert.Equal(0, store.Transactions);
    }

    [Fact]
    public async Task ConsultaNula_LanzaExcepcion() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
}