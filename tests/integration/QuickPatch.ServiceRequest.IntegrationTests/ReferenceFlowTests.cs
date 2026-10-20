using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// Categorías y localidades servidas por ServiceRequest (contrato 1.8.0, ADR-025) contra PostgreSQL y Kafka reales: la
/// réplica guarda la descripción que llega en <c>catalog.category-changed</c> (quickpatch-kafka v0.8.0) y la lista corre como
/// <c>service_request_app</c> bajo RLS.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class ReferenceFlowTests(ServiceRequestEnvironment env)
{
    private static string CategoryChanged(Guid tenant, Guid category, string name, string? description, DateTimeOffset updatedAt) =>
        JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "catalog.category-changed",
            eventVersion = 1,
            occurredAt = updatedAt,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "catalog-service",
            data = new { categoryId = category, name, description, active = true, updatedAt },
        });

    [Fact]
    public async Task LaReplicaGuardaLaDescripcion_YLaListaSoloMuestraElTenantDelToken()
    {
        var (tenant, otherTenant, category, foreign) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await env.PublishAsync("catalog.category-changed", category.ToString(), CategoryChanged(tenant, category, "Cerrajería", "Chapas y llaves.", now));
        await env.PublishAsync("catalog.category-changed", foreign.ToString(), CategoryChanged(otherTenant, foreign, "Ajena", null, now));
        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>(
                "SELECT count(*) FROM service_request_categories WHERE category_id IN (@a, @b)", ("a", category), ("b", foreign)) == 2,
            "la réplica recibe las dos categorías");

        using var http = env.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, "cliente"));
        using var response = await http.GetAsync(new Uri("/v1/service-requests/categories", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        Assert.Equal(category, item.GetProperty("id").GetGuid());
        Assert.Equal("Chapas y llaves.", item.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Localidades_LasVeinteDeLaCopia_SinToken()
    {
        using var http = env.App.CreateClient();

        using var response = await http.GetAsync(new Uri("/v1/service-requests/localities", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        Assert.Equal(20, items.Count);
        Assert.Equal("01", items[0].GetProperty("code").GetString());
        Assert.Equal(["code", "name"], items[0].EnumerateObject().Select(p => p.Name));
    }
}