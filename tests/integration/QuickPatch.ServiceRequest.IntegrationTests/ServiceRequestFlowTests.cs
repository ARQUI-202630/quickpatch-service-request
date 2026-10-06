using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Confluent.Kafka;

using Npgsql;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// Flujo de SCRUM-27 contra PostgreSQL/PostGIS y Kafka reales: réplica de categorías por evento, creación,
/// Outbox publicado en Kafka según el contrato, idempotencia y aislamiento entre tenants con RLS.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class ServiceRequestFlowTests(ServiceRequestEnvironment env)
{
    private static string CategoryChanged(Guid eventId, Guid tenant, Guid category, bool active, DateTimeOffset updatedAt) =>
        JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "catalog.category-changed",
            eventVersion = 1,
            occurredAt = updatedAt,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "catalog-service",
            data = new { categoryId = category, name = "Plomería", active, updatedAt },
        });

    private async Task<Guid> SeedCategoryAsync(Guid tenant)
    {
        var category = Guid.NewGuid();
        await env.PublishAsync("catalog.category-changed", category.ToString(), CategoryChanged(Guid.NewGuid(), tenant, category, true, DateTimeOffset.UtcNow));
        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>("SELECT count(*) FROM service_request_categories WHERE category_id = @c", ("c", category)) == 1,
            "la réplica recibe la categoría");
        return category;
    }

    private HttpClient Client(Guid user, Guid tenant, string role = "cliente")
    {
        var client = env.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return client;
    }

    private static object Body(Guid category) => new
    {
        categoryId = category,
        description = "Fuga de agua debajo del lavaplatos",
        location = new { latitude = 4.6486, longitude = -74.063 },
        addressText = "Calle 63 # 9-45, apartamento 502",
    };

    [Fact]
    public async Task HealthReady_ConPostgreSql_RespondeOk()
    {
        using var client = env.App.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Crear_PersisteYPublicaServiceRequestCreatedSegunElContrato()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var category = await SeedCategoryAsync(tenant);
        using var client = Client(user, tenant);
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlation.ToString());

        using var response = await client.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body(category));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("buscando_tecnico", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(tenant, await env.ScalarAdminAsync<Guid>("SELECT tenant_id FROM service_requests WHERE id = @id", ("id", id)));

        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>("SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND published_at IS NOT NULL", ("id", id)) == 1,
            "el Outbox marca el evento como publicado");

        var message = ConsumeFor("service-request.created", id.ToString());
        Assert.Equal(id.ToString(), message.Message.Key);
        using var json = JsonDocument.Parse(message.Message.Value);
        var root = json.RootElement;
        var outboxId = await env.ScalarAdminAsync<Guid>("SELECT id FROM outbox_events WHERE aggregate_id = @id", ("id", id));
        Assert.Equal(outboxId, root.GetProperty("eventId").GetGuid());
        Assert.Equal("service-request.created", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("eventVersion").GetInt32());
        Assert.Equal(correlation, root.GetProperty("correlationId").GetGuid());
        Assert.Equal(tenant, root.GetProperty("tenantId").GetGuid());
        Assert.Equal("service-request-service", root.GetProperty("producer").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(id, data.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal(user, data.GetProperty("clientId").GetGuid());
        Assert.Equal(category, data.GetProperty("categoryId").GetGuid());
        Assert.Equal("Fuga de agua debajo del lavaplatos", data.GetProperty("description").GetString());
        Assert.False(data.TryGetProperty("addressText", out _));
    }

    [Fact]
    public async Task EventoDeCategoriaRepetido_SeAplicaUnaSolaVez()
    {
        var tenant = Guid.NewGuid();
        var category = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var value = CategoryChanged(eventId, tenant, category, true, DateTimeOffset.UtcNow);

        await env.PublishAsync("catalog.category-changed", category.ToString(), value);
        await env.PublishAsync("catalog.category-changed", category.ToString(), value);
        await env.PublishAsync("catalog.category-changed", category.ToString(), "esto no es JSON");
        var marker = await SeedCategoryAsync(tenant);

        Assert.NotEqual(Guid.Empty, marker);
        Assert.Equal(1, await env.ScalarAdminAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @e", ("e", eventId)));
        Assert.Equal(1, await env.ScalarAdminAsync<long>("SELECT count(*) FROM service_request_categories WHERE category_id = @c", ("c", category)));
    }

    [Fact]
    public async Task OtroTenant_NoVeLaSolicitudNiPuedeUsarLaCategoria()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var user = Guid.NewGuid();
        var categoryA = await SeedCategoryAsync(tenantA);
        using var clientA = Client(user, tenantA);
        using var created = await clientA.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body(categoryA));
        var location = created.Headers.Location!;

        using var clientB = Client(user, tenantB);
        using var get = await clientB.GetAsync(location);
        using var create = await clientB.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body(categoryA));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
    }

    [Fact]
    public async Task Rls_ElRolDelServicioSoloVeYEscribeEnSuTenant()
    {
        var tenantA = Guid.NewGuid();
        var categoryA = await SeedCategoryAsync(tenantA);
        using var clientA = Client(Guid.NewGuid(), tenantA);
        using var created = await clientA.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), Body(categoryA));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0, await CountAsync(connection, null));
        Assert.Equal(0, await CountAsync(connection, Guid.NewGuid()));
        Assert.True(await CountAsync(connection, tenantA) >= 1);

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", Guid.NewGuid().ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO service_request_categories (category_id, tenant_id, name, active, updated_at) VALUES (@id, @tenant, 'x', true, now())",
            connection,
            transaction);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("tenant", tenantA);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, Guid? tenant)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        if (tenant is not null)
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t, true)", connection, transaction);
            set.Parameters.AddWithValue("t", tenant.Value.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM service_requests", connection, transaction);
        var result = (long)(await count.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private ConsumeResult<string, string> ConsumeFor(string topic, string key)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = env.KafkaServers,
            GroupId = $"pruebas-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limit)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message.Key == key)
            {
                consumer.Close();
                return result;
            }
        }

        throw new TimeoutException($"No llegó a {topic} un mensaje con la clave {key}.");
    }
}