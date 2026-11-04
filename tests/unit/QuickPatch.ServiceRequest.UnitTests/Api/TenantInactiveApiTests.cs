using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Tenants;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>POST /v1/service-requests con un tenant desactivado (RN-T1, SCRUM-348, SCRUM-510).</summary>
public class TenantInactiveApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid category = Guid.NewGuid();

    private HttpClient ClientOfTenant()
    {
        lock (factory.Store)
        {
            factory.Store.Categories.Add(new CategoryReplica(category, tenant, "Plomería", true, DateTimeOffset.UtcNow));
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, "cliente"));
        return client;
    }

    private object Body() => new
    {
        categoryId = category,
        description = "Fuga de agua en la cocina",
        location = new { latitude = 4.6486, longitude = -74.063 },
        addressText = "Calle 63 # 9-45, apto 502",
    };

    [Fact]
    public async Task TenantInactivo_Responde422TenantInactivoConUnTokenVigente()
    {
        lock (factory.Store)
        {
            factory.Store.TenantStatuses.Add(new TenantStatusReplica(tenant, TenantStatusReplica.Inactive, DateTimeOffset.UtcNow));
        }

        using var client = ClientOfTenant();
        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("https://quickpatch.internal/problems/tenant-inactivo", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(422, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("tenant is disabled", json.RootElement.GetProperty("detail").GetString());
        Assert.True(json.RootElement.TryGetProperty("correlationId", out _));
        Assert.DoesNotContain(factory.Store.ServiceRequests, x => x.TenantId == tenant);
    }

    [Fact]
    public async Task TenantActivoOSinFila_Crea201()
    {
        using var client = ClientOfTenant();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}