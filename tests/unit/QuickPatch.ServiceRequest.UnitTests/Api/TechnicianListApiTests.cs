using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary><c>GET /v1/technicians/me/service-requests</c> (contrato 1.2.0, RF-17, SCRUM-464).</summary>
public class TechnicianListApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private static readonly Uri List = new("/v1/technicians/me/service-requests", UriKind.Relative);

    private DomainServiceRequest Seed(Guid tenant, Guid technician, string status)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, Guid.NewGuid(), Guid.NewGuid(), technician, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45, apto 502", status, Now, Now.AddMinutes(1));
        lock (factory.Store)
        {
            factory.Store.ServiceRequests.Add(request);
        }

        return request;
    }

    private HttpClient As(Guid user, Guid tenant, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Tecnico_200SinDireccionUbicacionNiCliente()
    {
        var tenant = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var request = Seed(tenant, technician, ServiceRequestStatus.Asignado);
        Seed(tenant, Guid.NewGuid(), ServiceRequestStatus.Asignado);
        using var http = As(technician, tenant, "tecnico");

        using var response = await http.GetAsync(List);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(1, json.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(20, json.GetProperty("size").GetInt32());
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(request.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("asignado", item.GetProperty("status").GetString());
        Assert.Equal(request.CategoryId, item.GetProperty("categoryId").GetGuid());
        Assert.Equal("Fuga de agua en la cocina", item.GetProperty("description").GetString());
        Assert.Equal(Now.AddMinutes(1), item.GetProperty("assignedAt").GetDateTimeOffset());

        // RN-SR13: el historial no trae datos de contacto.
        Assert.Equal(
            ["assignedAt", "categoryId", "createdAt", "description", "id", "status"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FiltroYPaginacion()
    {
        var tenant = Guid.NewGuid();
        var technician = Guid.NewGuid();
        Seed(tenant, technician, ServiceRequestStatus.Asignado);
        Seed(tenant, technician, ServiceRequestStatus.Asignado);
        Seed(tenant, technician, ServiceRequestStatus.Pagado);
        using var http = As(technician, tenant, "tecnico");

        using var response = await http.GetAsync(new Uri($"{List}?status=asignado&page=2&size=1", UriKind.Relative));

        var json = await Json(response);
        Assert.Equal(2, json.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, json.GetProperty("page").GetInt32());
        Assert.Single(json.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("status=inventado", "status")]
    [InlineData("page=0", "page")]
    [InlineData("page=uno", "page")]
    [InlineData("size=101", "size")]
    [InlineData("size=-1", "size")]
    public async Task FiltrosInvalidos_400ConElCampo(string query, string campo)
    {
        using var http = As(Guid.NewGuid(), Guid.NewGuid(), "tecnico");

        using var response = await http.GetAsync(new Uri($"{List}?{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await Json(response);
        Assert.EndsWith("/validacion", json.GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.True(json.GetProperty("errors").TryGetProperty(campo, out _));
    }

    [Theory]
    [InlineData("cliente")]
    [InlineData("admin_tenant")]
    public async Task OtroRol_403(string role)
    {
        using var http = As(Guid.NewGuid(), Guid.NewGuid(), role);

        using var response = await http.GetAsync(List);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var http = factory.CreateClient();

        using var response = await http.GetAsync(List);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}