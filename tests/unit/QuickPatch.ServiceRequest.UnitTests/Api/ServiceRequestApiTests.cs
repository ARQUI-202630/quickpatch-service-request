using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>API completa con los puertos en memoria: autenticación, validación, reglas y contrato HTTP.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public InMemoryStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
        builder.UseSetting("ConnectionStrings:ServiceRequest", "Host=sin-base-en-pruebas-unitarias");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Store);
            services.AddSingleton<ITenantUnitOfWork>(Store);
            services.AddSingleton<IServiceRequestRepository>(Store);
            services.AddSingleton<ICategoryReplicaRepository>(Store);
            services.AddSingleton<IProcessedEventRepository>(Store);
            services.AddSingleton<IOutbox>(Store);
            services.Configure<HealthCheckServiceOptions>(o => o.Registrations.Clear());
        });
    }
}

public class ServiceRequestApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();

    private HttpClient Client(string role = "cliente", Guid? user = null, Guid? tenant = null)
    {
        lock (factory.Store)
        {
            if (!factory.Store.Categories.Any(c => c.CategoryId == Category))
            {
                factory.Store.Categories.Add(new CategoryReplica(Category, Tenant, "Plomería", true, DateTimeOffset.UtcNow));
            }
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(user ?? Guid.NewGuid(), tenant ?? Tenant, role));
        return client;
    }

    private static object Body(double latitude = 4.6486, double longitude = -74.063, Guid? category = null, string description = "Fuga de agua en la cocina") => new
    {
        categoryId = category ?? Category,
        description,
        location = new { latitude, longitude },
        addressText = "Calle 63 # 9-45, apto 502",
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Crear_DatosValidos_Responde201ConLaSolicitud()
    {
        var correlation = Guid.NewGuid();
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlation.ToString());

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(correlation.ToString(), response.Headers.GetValues("X-Correlation-Id").Single());
        var json = await Json(response);
        Assert.Equal("buscando_tecnico", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("technicianId").ValueKind);
        Assert.Equal($"/v1/service-requests/{json.GetProperty("id").GetGuid()}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Crear_SinToken_Responde401ComoProblem()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Crear_TokenExpirado_Responde401()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(Guid.NewGuid(), Tenant, "cliente", DateTimeOffset.UtcNow.AddMinutes(-5)));

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Crear_RolTecnico_Responde403()
    {
        using var client = Client(role: "tecnico");

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Crear_FueraDeBogota_Responde422ConTipoDelContrato()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body(6.2442, -75.5812));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var json = await Json(response);
        Assert.Equal("https://quickpatch.internal/problems/ubicacion-fuera-de-cobertura", json.GetProperty("type").GetString());
        Assert.True(json.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Crear_CategoriaInexistente_Responde422()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body(category: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/categoria-no-disponible", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Crear_DescripcionCorta_Responde400ConErrorPorCampo()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body(description: "corta"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("https://quickpatch.internal/problems/validacion", json.GetProperty("type").GetString());
        Assert.True(json.GetProperty("errors").TryGetProperty("description", out _));
    }

    [Fact]
    public async Task Crear_SinCategoriaNiUbicacion_Responde400()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new { description = "Fuga de agua en la cocina" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await Json(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("categoryId", out _));
        Assert.True(errors.TryGetProperty("location", out _));
    }

    [Fact]
    public async Task Crear_CampoDesconocido_Responde400()
    {
        using var client = Client();
        using var content = new StringContent(
            $$"""{"categoryId":"{{Category}}","description":"Fuga de agua en la cocina","location":{"latitude":4.6,"longitude":-74.1},"addressText":"Calle 63 # 9-45","tenantId":"{{Guid.NewGuid()}}"}""",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(new Uri("/v1/service-requests", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Consultar_PropiaDevuelve200_DeOtroClienteOTenant404()
    {
        var owner = Guid.NewGuid();
        using var client = Client(user: owner);
        using var created = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());
        var location = created.Headers.Location!;

        using var own = await client.GetAsync(location);
        using var otherClient = Client();
        using var otherTenant = Client(user: owner, tenant: Guid.NewGuid());
        using var byOtherClient = await otherClient.GetAsync(location);
        using var byOtherTenant = await otherTenant.GetAsync(location);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("Fuga de agua en la cocina", (await Json(own)).GetProperty("description").GetString());
        Assert.Equal(HttpStatusCode.NotFound, byOtherClient.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOtherTenant.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/no-encontrado", (await Json(byOtherTenant)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task TokenSinTenant_Responde401()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(Guid.NewGuid(), Guid.Empty, "cliente"));

        using var response = await client.GetAsync(new Uri($"/v1/service-requests/{Guid.NewGuid()}", UriKind.Relative));
        using var post = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [Fact]
    public async Task CorrelationIdInvalido_GeneraUnoNuevo()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "no-es-uuid");

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Guid.TryParse(response.Headers.GetValues("X-Correlation-Id").Single(), out _));
    }

    [Fact]
    public async Task HealthReady_RespondeOk()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}