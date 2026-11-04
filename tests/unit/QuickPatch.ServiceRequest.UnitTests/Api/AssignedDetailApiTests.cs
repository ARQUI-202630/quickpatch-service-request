using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary><c>GET /v1/service-requests/{id}</c> para el técnico asignado (contrato 1.1.0, SCRUM-34 y SCRUM-91).</summary>
public class AssignedDetailApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private (DomainServiceRequest Request, Guid Client, Guid Technician) Seed(string status)
    {
        var client = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), Tenant, client, Guid.NewGuid(), technician, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45, apto 502", status, Now, Now.AddMinutes(1));
        lock (factory.Store)
        {
            factory.Store.ServiceRequests.Add(request);
            factory.Directory.Users[client] = new UserContact(client, "Ana Gómez", "+573009876543");
            factory.Directory.Users[technician] = new UserContact(technician, "Carlos Pérez", "3101234567");
        }

        return (request, client, technician);
    }

    private HttpClient As(Guid user, string role, Guid? tenant = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant ?? Tenant, role));
        return client;
    }

    private static Uri Detail(Guid id) => new($"/v1/service-requests/{id}", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task TecnicoAsignado_200ConDireccionYContactoDelCliente()
    {
        var (request, client, technician) = Seed(ServiceRequestStatus.Asignado);
        using var http = As(technician, "tecnico");

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("asignado", json.GetProperty("status").GetString());
        Assert.Equal("Calle 63 # 9-45, apto 502", json.GetProperty("addressText").GetString());
        Assert.Equal(Now.AddMinutes(1), json.GetProperty("assignedAt").GetDateTimeOffset());
        Assert.Equal(technician, json.GetProperty("technician").GetProperty("id").GetGuid());
        Assert.Equal("Carlos Pérez", json.GetProperty("technician").GetProperty("fullName").GetString());
        Assert.Equal(client, json.GetProperty("client").GetProperty("id").GetGuid());
        Assert.Equal("Ana Gómez", json.GetProperty("client").GetProperty("fullName").GetString());
        Assert.Equal("+573009876543", json.GetProperty("client").GetProperty("phone").GetString());
    }

    [Fact]
    public async Task TecnicoAsignado_FueraDelRango_403SinDatos()
    {
        var (request, _, technician) = Seed(ServiceRequestStatus.Pagado);
        using var http = As(technician, "tecnico");

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("https://quickpatch.internal/problems/no-autorizado", json.GetProperty("type").GetString());
        Assert.True(json.TryGetProperty("correlationId", out _));
        Assert.False(json.TryGetProperty("addressText", out _));
    }

    [Fact]
    public async Task OtroTecnicoUOtroTenant_404()
    {
        var (request, _, technician) = Seed(ServiceRequestStatus.Asignado);
        using var otro = As(Guid.NewGuid(), "tecnico");
        using var otroTenant = As(technician, "tecnico", Guid.NewGuid());

        using var byOther = await otro.GetAsync(Detail(request.Id));
        using var byOtherTenant = await otroTenant.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.NotFound, byOther.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOtherTenant.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/no-encontrado", (await Json(byOther)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task ClienteDueno_VeAlTecnicoYClienteNull()
    {
        var (request, client, technician) = Seed(ServiceRequestStatus.Asignado);
        using var http = As(client, "cliente");

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(technician, json.GetProperty("technicianId").GetGuid());
        Assert.Equal("Carlos Pérez", json.GetProperty("technician").GetProperty("fullName").GetString());
        Assert.False(json.GetProperty("technician").TryGetProperty("phone", out _));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("client").ValueKind);
    }

    [Theory]
    [InlineData("proveedor")]
    [InlineData("admin_plataforma")]
    public async Task RolSinAcceso_403(string role)
    {
        var (request, _, _) = Seed(ServiceRequestStatus.Asignado);
        using var http = As(Guid.NewGuid(), role);

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReenviaElTokenDelUsuarioAIdentity()
    {
        var (request, _, technician) = Seed(ServiceRequestStatus.EnProgreso);
        var token = TestTokens.Create(technician, Tenant, "tecnico");
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        lock (factory.Store)
        {
            Assert.Contains(factory.Directory.Calls, c => c.Token == token && c.Ids.Contains(technician));
        }
    }
}