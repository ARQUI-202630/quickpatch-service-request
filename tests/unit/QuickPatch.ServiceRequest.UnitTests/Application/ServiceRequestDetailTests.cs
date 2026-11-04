using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Detalle por rol (contrato 1.1.0, DD V4 8.3.4, RN-SR13, SCRUM-34).</summary>
public class ServiceRequestDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly FakeUserDirectory directory = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();
    private readonly Guid correlation = Guid.NewGuid();

    public ServiceRequestDetailTests()
    {
        directory.Users[client] = new UserContact(client, "Ana Gómez", "+573009876543");
        directory.Users[technician] = new UserContact(technician, "Carlos Pérez", "3101234567");
    }

    private GetServiceRequestHandler Handler() => new(store, store, directory);

    private DomainServiceRequest Seed(string status, Guid? assigned)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, client, Guid.NewGuid(), assigned, "Fuga de agua", new GeoPoint(4.6486, -74.063), "Calle 63", status, Now,
            assigned is null ? null : Now.AddMinutes(1));
        store.ServiceRequests.Add(request);
        return request;
    }

    private ServiceRequestViewer Viewer(Guid user, string role, Guid? tenantId = null) =>
        new(user, tenantId ?? tenant, role, "token-del-usuario", correlation);

    [Fact]
    public async Task TecnicoAsignado_VeElContactoDelCliente()
    {
        var request = Seed(ServiceRequestStatus.Asignado, technician);

        var result = await Handler().HandleAsync(Viewer(technician, "tecnico"), request.Id, CancellationToken.None);

        var found = Assert.IsType<GetServiceRequestResult.Found>(result);
        Assert.Equal(new TechnicianView(technician, "Carlos Pérez"), found.Technician);
        Assert.Equal(new ClientView(client, "Ana Gómez", "+573009876543"), found.Client);
        var call = Assert.Single(directory.Calls);
        Assert.Equal("token-del-usuario", call.Token);
        Assert.Equal(correlation, call.CorrelationId);
        Assert.Equal([technician, client], call.Ids);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Pagado)]
    [InlineData(ServiceRequestStatus.Cancelado)]
    public async Task TecnicoAsignado_FueraDelRango_Forbidden(string status)
    {
        var request = Seed(status, technician);

        var result = await Handler().HandleAsync(Viewer(technician, "tecnico"), request.Id, CancellationToken.None);

        Assert.IsType<GetServiceRequestResult.Forbidden>(result);
        Assert.Empty(directory.Calls);
    }

    [Fact]
    public async Task OtroTecnico_NotFound()
    {
        var request = Seed(ServiceRequestStatus.Asignado, technician);

        var result = await Handler().HandleAsync(Viewer(Guid.NewGuid(), "tecnico"), request.Id, CancellationToken.None);

        Assert.IsType<GetServiceRequestResult.NotFound>(result);
    }

    [Fact]
    public async Task TecnicoDeOtroTenant_NotFound()
    {
        var request = Seed(ServiceRequestStatus.Asignado, technician);

        var result = await Handler().HandleAsync(Viewer(technician, "tecnico", Guid.NewGuid()), request.Id, CancellationToken.None);

        Assert.IsType<GetServiceRequestResult.NotFound>(result);
    }

    [Fact]
    public async Task Cliente_VeElNombreDelTecnicoYNuncaElBloqueCliente()
    {
        var request = Seed(ServiceRequestStatus.Asignado, technician);

        var result = await Handler().HandleAsync(Viewer(client, "cliente"), request.Id, CancellationToken.None);

        var found = Assert.IsType<GetServiceRequestResult.Found>(result);
        Assert.Equal(new TechnicianView(technician, "Carlos Pérez"), found.Technician);
        Assert.Null(found.Client);
        Assert.Equal([technician], Assert.Single(directory.Calls).Ids);
    }

    [Fact]
    public async Task Cliente_SinAsignacion_NoConsultaIdentity()
    {
        var request = Seed(ServiceRequestStatus.BuscandoTecnico, null);

        var result = await Handler().HandleAsync(Viewer(client, "cliente"), request.Id, CancellationToken.None);

        var found = Assert.IsType<GetServiceRequestResult.Found>(result);
        Assert.Null(found.Technician);
        Assert.Null(found.Client);
        Assert.Empty(directory.Calls);
    }

    [Fact]
    public async Task IdentityNoResponde_LosNombresLleganEnNull()
    {
        directory.Fail = true;
        var request = Seed(ServiceRequestStatus.EnProgreso, technician);

        var result = await Handler().HandleAsync(Viewer(technician, "tecnico"), request.Id, CancellationToken.None);

        var found = Assert.IsType<GetServiceRequestResult.Found>(result);
        Assert.Equal(new TechnicianView(technician, null), found.Technician);
        Assert.Equal(new ClientView(client, null, null), found.Client);
    }

    [Theory]
    [InlineData("proveedor")]
    [InlineData("admin_tenant")]
    public async Task OtroRol_Forbidden(string role)
    {
        var request = Seed(ServiceRequestStatus.Asignado, technician);

        var result = await Handler().HandleAsync(Viewer(Guid.NewGuid(), role), request.Id, CancellationToken.None);

        Assert.IsType<GetServiceRequestResult.Forbidden>(result);
    }

    [Fact]
    public async Task SinViewer_LanzaExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, Guid.NewGuid(), CancellationToken.None));
    }
}