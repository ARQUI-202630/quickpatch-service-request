using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>
/// Panel operativo (contrato 1.4.0): <c>GET /v1/service-requests</c> y <c>GET /v1/admin/service-requests/summary</c> para el
/// <c>admin_tenant</c>, y <c>GET /v1/platform/service-requests/summary</c> para el <c>admin_plataforma</c>.
/// </summary>
public class AdminPanelApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri List = new("/v1/service-requests", UriKind.Relative);
    private static readonly Uri Summary = new("/v1/admin/service-requests/summary", UriKind.Relative);
    private static readonly Uri Platform = new("/v1/platform/service-requests/summary", UriKind.Relative);

    private DomainServiceRequest Seed(Guid tenant, string status, Guid? technician = null)
    {
        var now = DateTimeOffset.UtcNow;
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, Guid.NewGuid(), Guid.NewGuid(), technician, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45, apto 502", status, now, technician is null ? null : now);
        lock (factory.Store)
        {
            factory.Store.ServiceRequests.Add(request);
        }

        return request;
    }

    private HttpClient As(Guid tenant, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, role));
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static readonly string[] Estados =
        ["buscando_tecnico", "en_espera", "asignado", "cotizado", "cotizacion_aceptada", "en_progreso", "completado", "pagado", "cancelado"];

    [Fact]
    public async Task Lista_AdminTenant_SoloSuTenant_SinDireccionUbicacionNiContacto()
    {
        var tenant = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var request = Seed(tenant, ServiceRequestStatus.Asignado, technician);
        Seed(Guid.NewGuid(), ServiceRequestStatus.Asignado);
        using var http = As(tenant, "admin_tenant");

        using var response = await http.GetAsync(List);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(1, json.GetProperty("totalItems").GetInt32());
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(request.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(request.ClientId, item.GetProperty("clientId").GetGuid());
        Assert.Equal(technician, item.GetProperty("technicianId").GetGuid());
        Assert.Equal(
            ["assignedAt", "categoryId", "clientId", "createdAt", "id", "status", "technicianId"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Lista_SinTecnico_TechnicianIdYAssignedAtEnNull()
    {
        var tenant = Guid.NewGuid();
        Seed(tenant, ServiceRequestStatus.BuscandoTecnico);
        using var http = As(tenant, "admin_tenant");

        var item = Assert.Single((await Json(await http.GetAsync(List))).GetProperty("items").EnumerateArray());

        Assert.Equal(JsonValueKind.Null, item.GetProperty("technicianId").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("assignedAt").ValueKind);
    }

    [Theory]
    [InlineData("status=inventado", "status")]
    [InlineData("page=0", "page")]
    [InlineData("size=uno", "size")]
    [InlineData("size=101", "size")]
    public async Task Lista_FiltroInvalido_400ConElCampo(string query, string campo)
    {
        using var http = As(Guid.NewGuid(), "admin_tenant");

        using var response = await http.GetAsync(new Uri($"{List}?{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty(campo, out _));
    }

    [Fact]
    public async Task Conteos_AdminTenant_NueveEstados_SoloSuTenant()
    {
        var tenant = Guid.NewGuid();
        Seed(tenant, ServiceRequestStatus.Asignado);
        Seed(tenant, ServiceRequestStatus.Asignado);
        Seed(tenant, ServiceRequestStatus.Cancelado);
        Seed(Guid.NewGuid(), ServiceRequestStatus.Asignado);
        using var http = As(tenant, "admin_tenant");

        using var response = await http.GetAsync(Summary);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        var byStatus = json.GetProperty("byStatus");
        Assert.Equal(Estados, byStatus.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, byStatus.GetProperty("asignado").GetInt32());
        Assert.Equal(1, byStatus.GetProperty("cancelado").GetInt32());
        Assert.Equal(0, byStatus.GetProperty("pagado").GetInt32());
        Assert.Equal(3, json.GetProperty("createdToday").GetInt32());
        Assert.True(json.TryGetProperty("generatedAt", out _));
    }

    [Fact]
    public async Task Plataforma_AdminPlataforma_SoloConteosPorTenant()
    {
        var tenant = Guid.NewGuid();
        Seed(tenant, ServiceRequestStatus.EnEspera);
        using var http = As(Guid.NewGuid(), "admin_plataforma");

        using var response = await http.GetAsync(new Uri($"{Platform}?tenantId={tenant}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single((await Json(response)).GetProperty("tenants").EnumerateArray());
        Assert.Equal(["byStatus", "createdToday", "tenantId"], entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(tenant, entry.GetProperty("tenantId").GetGuid());
        Assert.Equal(1, entry.GetProperty("byStatus").GetProperty("en_espera").GetInt32());
    }

    [Theory]
    [InlineData("tenantId=no-es-uuid")]
    [InlineData("tenantId=00000000-0000-0000-0000-000000000000")]
    public async Task Plataforma_TenantInvalido_400(string query)
    {
        using var http = As(Guid.NewGuid(), "admin_plataforma");

        using var response = await http.GetAsync(new Uri($"{Platform}?{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("tenantId", out _));
    }

    [Theory]
    [InlineData("/v1/service-requests", "cliente")]
    [InlineData("/v1/service-requests", "tecnico")]
    [InlineData("/v1/service-requests", "admin_plataforma")]
    [InlineData("/v1/admin/service-requests/summary", "cliente")]
    [InlineData("/v1/admin/service-requests/summary", "admin_plataforma")]
    [InlineData("/v1/platform/service-requests/summary", "admin_tenant")]
    [InlineData("/v1/platform/service-requests/summary", "cliente")]
    public async Task OtroRol_403(string path, string role)
    {
        using var http = As(Guid.NewGuid(), role);

        using var response = await http.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/service-requests")]
    [InlineData("/v1/admin/service-requests/summary")]
    [InlineData("/v1/platform/service-requests/summary")]
    public async Task SinToken_401(string path)
    {
        using var http = factory.CreateClient();

        using var response = await http.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}