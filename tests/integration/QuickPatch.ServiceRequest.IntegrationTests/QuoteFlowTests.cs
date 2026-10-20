using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Infrastructure.Scheduling;
using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-488 contra PostgreSQL y Redis reales: la cotización corre como <c>service_request_app</c> bajo RLS, los índices
/// únicos impiden dos pendientes, el vencimiento encuentra las vencidas con <c>service_request_scheduler</c> y se coordina
/// con el candado de Redis del usuario ACL del servicio.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class QuoteFlowTests(ServiceRequestEnvironment env)
{
    private async Task<(Guid Id, Guid Tenant, Guid Client, Guid Technician)> InsertAssignedAsync(string status = "asignado")
    {
        var (id, tenant, client, technician) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, technician_id, description, location, address_text, " +
            "status, created_at, assigned_at) " +
            $"VALUES ('{id}', '{tenant}', '{client}', '{Guid.NewGuid()}', '{technician}', 'Fuga de agua en la cocina', " +
            $"ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', '{status}', now(), now());");
        return (id, tenant, client, technician);
    }

    private HttpClient As(Guid user, Guid tenant, string role)
    {
        var http = env.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return http;
    }

    private static Uri Quotes(Guid id) => new($"/v1/service-requests/{id}/quotes", UriKind.Relative);

    private static object Body => new { laborAmount = "120000.00", materialsAmount = "65000.00", detail = "Cambio de sifón." };

    [Fact]
    public async Task Emite_Y_Acepta_ConLaBaseReal()
    {
        var (id, tenant, client, technician) = await InsertAssignedAsync();
        using var asTechnician = As(technician, tenant, "tecnico");
        using var asClient = As(client, tenant, "cliente");

        using var issued = await asTechnician.PostAsJsonAsync(Quotes(id), Body);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var quoteId = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // total_amount es una columna generada.
        Assert.Equal(185000.00m, await env.ScalarAdminAsync<decimal>("SELECT total_amount FROM quotes WHERE id = @id", ("id", quoteId)));
        Assert.Equal("cotizado", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.quoted'", ("id", id)));

        using var accepted = await asClient.PostAsync(new Uri($"{Quotes(id)}/{quoteId}/accept", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("aceptada", await env.ScalarAdminAsync<string>("SELECT status FROM quotes WHERE id = @id", ("id", quoteId)));
        Assert.Equal("cotizacion_aceptada", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.quote-accepted'", ("id", id)));
    }

    [Fact]
    public async Task OtroTenant_NoVeNiRespondeLaCotizacion()
    {
        var (id, tenant, client, technician) = await InsertAssignedAsync();
        using var asTechnician = As(technician, tenant, "tecnico");
        using var issued = await asTechnician.PostAsJsonAsync(Quotes(id), Body);
        var quoteId = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // Mismo cliente y mismo técnico, con el token de otro tenant.
        var otherTenant = Guid.NewGuid();
        using var otherClient = As(client, otherTenant, "cliente");
        using var otherTechnician = As(technician, otherTenant, "tecnico");

        using var list = await otherClient.GetAsync(Quotes(id));
        using var accept = await otherClient.PostAsync(new Uri($"{Quotes(id)}/{quoteId}/accept", UriKind.Relative), null);
        using var issue = await otherTechnician.PostAsJsonAsync(Quotes(id), Body);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, issue.StatusCode);
        Assert.Equal("pendiente", await env.ScalarAdminAsync<string>("SELECT status FROM quotes WHERE id = @id", ("id", quoteId)));

        // RLS directo: bajo otro tenant la fila no existe para la aplicación.
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_tenant', '{otherTenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM quotes WHERE id = '{quoteId}'", connection, transaction);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DosPendientesALaVez_LaBaseLasImpide()
    {
        var (id, tenant, _, technician) = await InsertAssignedAsync("cotizado");
        const string insert = "INSERT INTO quotes (id, tenant_id, service_request_id, technician_id, labor_amount, materials_amount, " +
            "status, created_at, expires_at) VALUES (gen_random_uuid(), @t, @sr, @tec, 1, 0, 'pendiente', now(), now() + interval '1 day')";
        await using var connection = new NpgsqlConnection(env.AdminConnectionString);
        await connection.OpenAsync();
        foreach (var attempt in new[] { 1, 2 })
        {
            await using var command = new NpgsqlCommand(insert, connection);
            command.Parameters.AddWithValue("t", tenant);
            command.Parameters.AddWithValue("sr", id);
            command.Parameters.AddWithValue("tec", technician);
            if (attempt == 1)
            {
                await command.ExecuteNonQueryAsync();
            }
            else
            {
                // RN-Q2: índice único parcial ux_quotes_pending.
                Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }
    }

    [Fact]
    public async Task Vencida_ElProcesoProgramadoCancelaPorElSistema()
    {
        var (id, tenant, _, technician) = await InsertAssignedAsync();
        using var asTechnician = As(technician, tenant, "tecnico");
        using var issued = await asTechnician.PostAsJsonAsync(Quotes(id), Body);
        var quoteId = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // La cotización ya venció (RN-Q6); el proceso corre cada segundo en las pruebas.
        await env.ExecuteAdminAsync(
            $"UPDATE quotes SET created_at = now() - interval '2 days', expires_at = now() - interval '1 minute' WHERE id = '{quoteId}'");

        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<string>("SELECT status FROM quotes WHERE id = @id", ("id", quoteId)) == "expirada",
            "la cotización vencida pasa a expirada");

        Assert.Equal("cancelado", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal("sistema", await env.ScalarAdminAsync<string>("SELECT cancelled_by_type FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal("Cotización sin respuesta.", await env.ScalarAdminAsync<string>(
            "SELECT cancellation_reason FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.cancelled'", ("id", id)));
    }

    [Fact]
    public async Task ElRolDelProcesoProgramado_SoloLeeLasColumnasDeReferencia()
    {
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE service_request_scheduler", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var detail = new NpgsqlCommand("SELECT labor_amount FROM quotes LIMIT 1", connection, transaction);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => detail.ExecuteScalarAsync())).SqlState);
    }

    [Fact]
    public async Task ElCandadoDeRedis_EsDeUnaSolaReplica_YUsaLasClavesDelServicio()
    {
        var locks = env.App.Services.GetRequiredService<IDistributedLock>();
        Assert.IsType<RedisDistributedLock>(locks);

        var name = $"prueba-{Guid.NewGuid():N}";
        await using (var first = await locks.TryAcquireAsync(name, TimeSpan.FromSeconds(30), CancellationToken.None))
        {
            Assert.NotNull(first);
            Assert.Null(await locks.TryAcquireAsync(name, TimeSpan.FromSeconds(30), CancellationToken.None));
        }

        await using var again = await locks.TryAcquireAsync(name, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(again);
    }
}