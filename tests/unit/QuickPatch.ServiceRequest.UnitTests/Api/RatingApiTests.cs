using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary><c>POST /v1/service-requests/{id}/rating</c> (DD 8.3.11, RF-12, ADR-027).</summary>
public class RatingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private DomainServiceRequest Seed(Guid tenant, Guid client, string status)
    {
        var now = DateTimeOffset.UtcNow;
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, client, Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45", status, now.AddHours(-2), now.AddHours(-1));
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

    private static Uri RatingUri(Guid id) => new($"/v1/service-requests/{id}/rating", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Cliente_201ConLaCalificacion()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, client, ServiceRequestStatus.Pagado);
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 5, comment = "Puntual y ordenado." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(["comment", "ratedAt", "score", "serviceRequestId"], json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(5, json.GetProperty("score").GetInt32());
        Assert.Equal(request.Id, json.GetProperty("serviceRequestId").GetGuid());
    }

    [Fact]
    public async Task EmpresaContactoQueLaCreo_201()
    {
        var (tenant, user) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, user, ServiceRequestStatus.Pagado);
        using var http = As(user, tenant, "empresa_contacto");

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 4 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(response)).GetProperty("comment").ValueKind);
    }

    [Fact]
    public async Task Repetida_409YaCalificada()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, client, ServiceRequestStatus.Pagado);
        using var http = As(client, tenant, "cliente");
        (await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 5 })).Dispose();

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 2 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("https://quickpatch.internal/problems/ya-calificada", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task SinPagar_409TransicionInvalida()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, client, ServiceRequestStatus.Completado);
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 5 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/transicion-invalida", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task OtroClienteDelTenant_403()
    {
        var tenant = Guid.NewGuid();
        var request = Seed(tenant, Guid.NewGuid(), ServiceRequestStatus.Pagado);
        using var http = As(Guid.NewGuid(), tenant, "cliente");

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClienteDeOtroTenant_404()
    {
        var client = Guid.NewGuid();
        var request = Seed(Guid.NewGuid(), client, ServiceRequestStatus.Pagado);
        using var http = As(client, Guid.NewGuid(), "cliente");

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 5 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("tecnico")]
    [InlineData("admin_tenant")]
    public async Task OtrosRoles_403(string role)
    {
        var tenant = Guid.NewGuid();
        var request = Seed(tenant, Guid.NewGuid(), ServiceRequestStatus.Pagado);
        using var http = As(Guid.NewGuid(), tenant, role);

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PuntajeInvalido_400ConElCampo()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var request = Seed(tenant, client, ServiceRequestStatus.Pagado);
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(RatingUri(request.Id), new { score = 7 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("score", out _));
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync(RatingUri(Guid.NewGuid()), new { score = 5 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}