using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// RN-T1 (SCRUM-348, SCRUM-510) contra PostgreSQL/PostGIS y Kafka reales: Identity publica <c>tenant.status-changed</c>, ServiceRequest
/// guarda la réplica del estado y rechaza crear solicitudes de un tenant desactivado con 422 <c>tenant-inactivo</c>, aunque el
/// token siga vigente. Al reactivarlo vuelve a crear. Cubre idempotencia, eventos anticuados, mensajes inválidos y aislamiento.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class TenantStatusFlowTests(ServiceRequestEnvironment env)
{
    private const string Topic = "tenant.status-changed";

    private static string StatusEvent(Guid tenant, string status, DateTimeOffset updatedAt, Guid? eventId = null) =>
        JsonSerializer.Serialize(new
        {
            eventId = eventId ?? Guid.NewGuid(),
            eventType = Topic,
            eventVersion = 1,
            occurredAt = updatedAt,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "identity-service",
            data = new { tenantId = tenant, status, updatedAt },
        });

    /// <summary>Publica la categoría y espera a que la réplica la tenga: sin ella la creación respondería 422 categoria-no-disponible.</summary>
    private async Task<Guid> CategoryAsync(Guid tenant)
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
        return category;
    }

    private async Task<HttpResponseMessage> CreateAsync(Guid tenant, Guid category)
    {
        using var client = env.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, "cliente"));
        return await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new
        {
            categoryId = category,
            description = "Fuga de agua debajo del lavaplatos",
            location = new { latitude = 4.6486, longitude = -74.063 },
            addressText = "Calle 63 # 9-45, apartamento 502",
        });
    }

    private async Task PublishStatusAsync(Guid tenant, string status, DateTimeOffset updatedAt, Guid? eventId = null) =>
        await env.PublishAsync(Topic, tenant.ToString(), StatusEvent(tenant, status, updatedAt, eventId));

    private Task WaitReplicaAsync(Guid tenant, string status) =>
        ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<string>("SELECT status FROM tenant_status_replica WHERE tenant_id = @t", ("t", tenant)) == status,
            $"la réplica del tenant queda {status}");

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("type").GetString();

    [Fact]
    public async Task TenantDesactivado_RechazaCrear_AunConTokenVigente_YAlReactivarloVuelveACrear()
    {
        var tenant = Guid.NewGuid();
        var category = await CategoryAsync(tenant);
        using (var antes = await CreateAsync(tenant, category))
        {
            Assert.Equal(HttpStatusCode.Created, antes.StatusCode);
        }

        var t0 = DateTimeOffset.UtcNow;
        await PublishStatusAsync(tenant, "inactivo", t0);
        await WaitReplicaAsync(tenant, "inactivo");

        using (var rechazada = await CreateAsync(tenant, category))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rechazada.StatusCode);
            Assert.Equal("https://quickpatch.internal/problems/tenant-inactivo", await ProblemTypeAsync(rechazada));
        }

        Assert.Equal(1L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM service_requests WHERE tenant_id = @t", ("t", tenant)));

        await PublishStatusAsync(tenant, "activo", t0.AddSeconds(30));
        await WaitReplicaAsync(tenant, "activo");

        using var despues = await CreateAsync(tenant, category);
        Assert.Equal(HttpStatusCode.Created, despues.StatusCode);
    }

    [Fact]
    public async Task EventoAnticuado_NoDeshaceUnCambioMasReciente()
    {
        var tenant = Guid.NewGuid();
        var category = await CategoryAsync(tenant);
        var t0 = DateTimeOffset.UtcNow;
        await PublishStatusAsync(tenant, "inactivo", t0);
        await WaitReplicaAsync(tenant, "inactivo");
        await PublishStatusAsync(tenant, "activo", t0.AddMinutes(1));
        await WaitReplicaAsync(tenant, "activo");

        // Llega tarde un "inactivo" más viejo que el "activo" ya aplicado: se descarta (ADR-017), pero queda registrado.
        var viejo = Guid.NewGuid();
        await PublishStatusAsync(tenant, "inactivo", t0.AddSeconds(10), viejo);
        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @e", ("e", viejo)) == 1,
            "el evento anticuado queda registrado");

        Assert.Equal("activo", await env.ScalarAdminAsync<string>("SELECT status FROM tenant_status_replica WHERE tenant_id = @t", ("t", tenant)));
        using var response = await CreateAsync(tenant, category);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task EventoRepetido_SeAplicaUnaSolaVez()
    {
        var tenant = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;

        await PublishStatusAsync(tenant, "inactivo", t0, eventId);
        await PublishStatusAsync(tenant, "inactivo", t0, eventId);
        await WaitReplicaAsync(tenant, "inactivo");
        // Un tercer mensaje distinto, posterior, prueba que el consumidor ya pasó por el duplicado.
        var sentinel = Guid.NewGuid();
        await PublishStatusAsync(tenant, "activo", t0.AddSeconds(5), sentinel);
        await WaitReplicaAsync(tenant, "activo");

        Assert.Equal(1L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @e", ("e", eventId)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM tenant_status_replica WHERE tenant_id = @t", ("t", tenant)));
    }

    [Fact]
    public async Task MensajeInvalido_SeDescarta_YNoBloqueaLosSiguientes()
    {
        var tenant = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;

        await env.PublishAsync(Topic, tenant.ToString(), "esto no es json");
        await PublishStatusAsync(tenant, "suspendido", t0);   // estado fuera del contrato
        await env.PublishAsync(Topic, tenant.ToString(), JsonSerializer.Serialize(new     // data.tenantId distinto del tenantId del sobre
        {
            eventId = Guid.NewGuid(),
            eventType = Topic,
            eventVersion = 1,
            occurredAt = t0,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "identity-service",
            data = new { tenantId = Guid.NewGuid(), status = "inactivo", updatedAt = t0 },
        }));
        await PublishStatusAsync(tenant, "inactivo", t0.AddSeconds(1));

        await WaitReplicaAsync(tenant, "inactivo");
        Assert.Equal(1L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM tenant_status_replica WHERE tenant_id = @t", ("t", tenant)));
    }

    [Fact]
    public async Task UnTenantInactivo_NoAfectaALosDemas()
    {
        var inactivo = Guid.NewGuid();
        var otro = Guid.NewGuid();
        var categoryOtro = await CategoryAsync(otro);
        var categoryInactivo = await CategoryAsync(inactivo);

        await PublishStatusAsync(inactivo, "inactivo", DateTimeOffset.UtcNow);
        await WaitReplicaAsync(inactivo, "inactivo");

        using var delOtro = await CreateAsync(otro, categoryOtro);
        using var delInactivo = await CreateAsync(inactivo, categoryInactivo);

        Assert.Equal(HttpStatusCode.Created, delOtro.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delInactivo.StatusCode);
        Assert.Equal(0L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM tenant_status_replica WHERE tenant_id = @t", ("t", otro)));
    }
}