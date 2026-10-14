using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// Estado de la solicitud con los eventos de Matching (SCRUM-29, DD 7.4) contra PostgreSQL/PostGIS y Kafka reales:
/// en espera, reanudación, eventos en desorden, idempotencia, mensajes inválidos y aislamiento entre tenants.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class MatchingStatusFlowTests(ServiceRequestEnvironment env)
{
    private const string NoTechnician = "matching.no-technician-available";
    private const string Resumed = "matching.search-resumed";

    private static string MatchingEvent(string type, Guid tenant, Guid request, DateTimeOffset occurredAt, Guid? eventId = null) =>
        JsonSerializer.Serialize(new
        {
            eventId = eventId ?? Guid.NewGuid(),
            eventType = type,
            eventVersion = 1,
            occurredAt,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "matching-service",
            data = type == Resumed
                ? (object)new { serviceRequestId = request, clientId = Guid.NewGuid(), trigger = "disponibilidad" }
                : new { serviceRequestId = request, clientId = Guid.NewGuid(), attemptsCount = 0 },
        });

    private async Task<Guid> CreateRequestAsync(Guid tenant)
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

        using var client = env.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, "cliente"));
        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new
        {
            categoryId = category,
            description = "Fuga de agua debajo del lavaplatos",
            location = new { latitude = 4.6486, longitude = -74.063 },
            addressText = "Calle 63 # 9-45, apartamento 502",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<string?> StatusAsync(Guid request) =>
        env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", request));

    private Task WaitStatusAsync(Guid request, string status) =>
        ServiceRequestEnvironment.WaitUntilAsync(async () => await StatusAsync(request) == status, $"la solicitud pasa a {status}");

    private Task WaitProcessedAsync(Guid eventId) =>
        ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @e", ("e", eventId)) == 1,
            "el evento queda registrado");

    [Fact]
    public async Task SinTecnicosYReanudacion_CambianElEstadoSegunLaMaquinaDeEstados()
    {
        var tenant = Guid.NewGuid();
        var request = await CreateRequestAsync(tenant);
        var t0 = DateTimeOffset.UtcNow;

        await env.PublishAsync(NoTechnician, request.ToString(), MatchingEvent(NoTechnician, tenant, request, t0));
        await WaitStatusAsync(request, "en_espera");

        await env.PublishAsync(Resumed, request.ToString(), MatchingEvent(Resumed, tenant, request, t0.AddMinutes(2)));
        await WaitStatusAsync(request, "buscando_tecnico");

        // Un no-technician-available más viejo que llega tarde no devuelve la solicitud a en_espera.
        var tardio = Guid.NewGuid();
        await env.PublishAsync(NoTechnician, request.ToString(), MatchingEvent(NoTechnician, tenant, request, t0.AddMinutes(1), tardio));
        await WaitProcessedAsync(tardio);
        Assert.Equal("buscando_tecnico", await StatusAsync(request));
    }

    [Fact]
    public async Task EventoRepetidoOInvalido_NoBloqueaNiRepiteElEfecto()
    {
        var tenant = Guid.NewGuid();
        var request = await CreateRequestAsync(tenant);
        var eventId = Guid.NewGuid();
        var evento = MatchingEvent(NoTechnician, tenant, request, DateTimeOffset.UtcNow, eventId);

        await env.PublishAsync(NoTechnician, request.ToString(), "esto no es JSON");
        await env.PublishAsync(NoTechnician, request.ToString(), evento);
        await env.PublishAsync(NoTechnician, request.ToString(), evento);
        await WaitStatusAsync(request, "en_espera");

        var marcador = Guid.NewGuid();
        await env.PublishAsync(NoTechnician, request.ToString(), MatchingEvent(NoTechnician, tenant, Guid.NewGuid(), DateTimeOffset.UtcNow, marcador));
        await WaitProcessedAsync(marcador);
        Assert.Equal(1, await env.ScalarAdminAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @e", ("e", eventId)));
    }

    [Fact]
    public async Task EventoConOtroTenant_NoCambiaLaSolicitud()
    {
        var tenant = Guid.NewGuid();
        var request = await CreateRequestAsync(tenant);
        var ajeno = Guid.NewGuid();

        await env.PublishAsync(NoTechnician, request.ToString(), MatchingEvent(NoTechnician, Guid.NewGuid(), request, DateTimeOffset.UtcNow, ajeno));
        await WaitProcessedAsync(ajeno);

        Assert.Equal("buscando_tecnico", await StatusAsync(request));
        Assert.Equal(0L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM service_requests WHERE id = @id AND matching_updated_at IS NOT NULL", ("id", request)));
    }
}