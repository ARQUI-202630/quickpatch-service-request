using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-91 contra PostgreSQL/PostGIS, Kafka e Identity (falso, por HTTP) reales: matching.technician-assigned pasa
/// la solicitud a asignado y el técnico asignado consulta el detalle con el contacto del cliente.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class AssignedDetailFlowTests(ServiceRequestEnvironment env)
{
    private static string Event(string type, Guid tenant, object data, Guid? eventId = null) => JsonSerializer.Serialize(new
    {
        eventId = eventId ?? Guid.NewGuid(),
        eventType = type,
        eventVersion = 1,
        occurredAt = DateTimeOffset.UtcNow,
        correlationId = Guid.NewGuid(),
        tenantId = tenant,
        producer = "matching-service",
        data,
    });

    private HttpClient As(Guid user, Guid tenant, string role)
    {
        var client = env.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return client;
    }

    private async Task<(Guid Id, Guid Client)> CreateRequestAsync(Guid tenant)
    {
        var category = Guid.NewGuid();
        await env.PublishAsync("catalog.category-changed", category.ToString(), JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "catalog.category-changed",
            eventVersion = 1,
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "catalog-service",
            data = new { categoryId = category, name = "Plomería", active = true, updatedAt = DateTimeOffset.UtcNow },
        }));
        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>("SELECT count(*) FROM service_request_categories WHERE category_id = @c", ("c", category)) == 1,
            "la réplica recibe la categoría");

        var client = Guid.NewGuid();
        using var http = As(client, tenant, "cliente");
        using var response = await http.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new
        {
            categoryId = category,
            description = "Fuga de agua debajo del lavaplatos",
            location = new { latitude = 4.6486, longitude = -74.063 },
            addressText = "Calle 63 # 9-45, apartamento 502",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), client);
    }

    private Task<string?> StatusAsync(Guid id) =>
        env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id));

    [Fact]
    public async Task Asignacion_PorKafka_YDetalleDelTecnicoConContactoDelCliente()
    {
        var tenant = Guid.NewGuid();
        var (id, client) = await CreateRequestAsync(tenant);
        var technician = Guid.NewGuid();
        env.Identity.Users[client] = ("Ana Gómez", "+573009876543");
        env.Identity.Users[technician] = ("Carlos Pérez", "3101234567");

        var assigned = Event("matching.technician-assigned", tenant, new
        {
            serviceRequestId = id,
            clientId = client,
            attemptId = Guid.NewGuid(),
            technicianId = technician,
            assignedAt = DateTimeOffset.UtcNow,
        });
        await env.PublishAsync("matching.technician-assigned", id.ToString(), assigned);
        await ServiceRequestEnvironment.WaitUntilAsync(async () => await StatusAsync(id) == "asignado", "la solicitud pasa a asignado");

        Assert.Equal(technician, await env.ScalarAdminAsync<Guid>("SELECT technician_id FROM service_requests WHERE id = @id", ("id", id)));
        Assert.NotNull(await env.ScalarAdminAsync<DateTime?>("SELECT assigned_at FROM service_requests WHERE id = @id", ("id", id)));

        using var http = As(technician, tenant, "tecnico");
        var token = http.DefaultRequestHeaders.Authorization!.Parameter;
        using var response = await http.GetAsync(new Uri($"/v1/service-requests/{id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("asignado", json.GetProperty("status").GetString());
        Assert.Equal("Calle 63 # 9-45, apartamento 502", json.GetProperty("addressText").GetString());
        Assert.Equal("Carlos Pérez", json.GetProperty("technician").GetProperty("fullName").GetString());
        Assert.Equal("Ana Gómez", json.GetProperty("client").GetProperty("fullName").GetString());
        Assert.Equal("+573009876543", json.GetProperty("client").GetProperty("phone").GetString());
        Assert.Contains($"Bearer {token}", env.Identity.Authorizations);

        using var otro = As(Guid.NewGuid(), tenant, "tecnico");
        using var byOther = await otro.GetAsync(new Uri($"/v1/service-requests/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, byOther.StatusCode);
    }
}