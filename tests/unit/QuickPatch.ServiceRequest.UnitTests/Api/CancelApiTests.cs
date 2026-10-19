using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary><c>POST /v1/service-requests/{id}/cancel</c> (contrato 1.5.0, RF-36).</summary>
public class CancelApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private DomainServiceRequest Seed(Guid tenant, Guid client, string status)
    {
        var now = DateTimeOffset.UtcNow;
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, client, Guid.NewGuid(), null, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45", status, now);
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

    private static Uri CancelUri(Guid id) => new($"/v1/service-requests/{id}/cancel", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Cliente_200ConElContrato_YElDetalleTraeElMotivo()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, client, ServiceRequestStatus.BuscandoTecnico);
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(CancelUri(request.Id), new { reason = "Ya no necesito el servicio." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(["cancelledAt", "cancelledByType", "id", "status"], json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("cancelado", json.GetProperty("status").GetString());
        Assert.Equal("cliente", json.GetProperty("cancelledByType").GetString());

        var detail = await Json(await http.GetAsync(new Uri($"/v1/service-requests/{request.Id}", UriKind.Relative)));
        Assert.Equal("Ya no necesito el servicio.", detail.GetProperty("cancellationReason").GetString());
        Assert.Equal(JsonValueKind.String, detail.GetProperty("cancelledAt").ValueKind);
    }

    [Fact]
    public async Task AdminTenant_200ComoAdmin()
    {
        var tenant = Guid.NewGuid();
        var request = Seed(tenant, Guid.NewGuid(), ServiceRequestStatus.EnEspera);
        using var http = As(Guid.NewGuid(), tenant, "admin_tenant");

        using var response = await http.PostAsJsonAsync(CancelUri(request.Id), new { reason = "Cliente sin respuesta." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin_tenant", (await Json(response)).GetProperty("cancelledByType").GetString());
    }

    [Fact]
    public async Task YaIniciada_409TransicionInvalida()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, client, ServiceRequestStatus.EnProgreso);
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(CancelUri(request.Id), new { reason = "Ya no necesito el servicio." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith("/transicion-invalida", (await Json(response)).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinMotivo_400ConElCampo()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, client, ServiceRequestStatus.BuscandoTecnico);
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(CancelUri(request.Id), new { reason = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("reason", out _));
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
    }

    [Fact]
    public async Task OtroClienteUOtroTenant_404()
    {
        var tenant = Guid.NewGuid();
        var request = Seed(tenant, Guid.NewGuid(), ServiceRequestStatus.BuscandoTecnico);
        using var otroCliente = As(Guid.NewGuid(), tenant, "cliente");
        using var otroTenant = As(Guid.NewGuid(), Guid.NewGuid(), "admin_tenant");

        using var a = await otroCliente.PostAsJsonAsync(CancelUri(request.Id), new { reason = "Ya no lo necesito." });
        using var b = await otroTenant.PostAsJsonAsync(CancelUri(request.Id), new { reason = "Ya no lo necesito." });

        Assert.Equal(HttpStatusCode.NotFound, a.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
    }

    [Theory]
    [InlineData("tecnico")]
    [InlineData("proveedor")]
    [InlineData("admin_plataforma")]
    public async Task OtroRol_403(string role)
    {
        using var http = As(Guid.NewGuid(), Guid.NewGuid(), role);

        using var response = await http.PostAsJsonAsync(CancelUri(Guid.NewGuid()), new { reason = "Ya no lo necesito." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync(CancelUri(Guid.NewGuid()), new { reason = "Ya no lo necesito." });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}