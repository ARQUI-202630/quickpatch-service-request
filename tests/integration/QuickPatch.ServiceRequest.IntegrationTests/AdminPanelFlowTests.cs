using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-103 contra PostgreSQL real. El panel del <c>admin_tenant</c> corre como <c>service_request_app</c> bajo RLS; la
/// vista de plataforma adopta <c>service_request_platform</c> (DD 10.2), que cuenta todos los tenants pero solo puede leer
/// <c>tenant_id</c>, <c>status</c> y <c>created_at</c>.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class AdminPanelFlowTests(ServiceRequestEnvironment env)
{
    private Task InsertAsync(Guid tenant, string status, string createdAt = "now()") =>
        env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, description, location, address_text, status, created_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{tenant}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'Fuga de agua en la cocina', " +
            $"ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', '{status}', {createdAt});");

    private HttpClient As(Guid tenant, string role)
    {
        var http = env.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, role));
        return http;
    }

    [Fact]
    public async Task AdminTenant_ListaYConteos_NuncaTraenOtroTenant()
    {
        var tenant = Guid.NewGuid();
        var otro = Guid.NewGuid();
        await InsertAsync(tenant, "asignado");
        await InsertAsync(tenant, "cancelado", "now() - interval '3 days'");
        await InsertAsync(otro, "asignado");
        await InsertAsync(otro, "asignado");
        using var http = As(tenant, "admin_tenant");

        var lista = await http.GetFromJsonAsync<JsonElement>(new Uri("/v1/service-requests", UriKind.Relative));
        var conteos = await http.GetFromJsonAsync<JsonElement>(new Uri("/v1/admin/service-requests/summary", UriKind.Relative));

        Assert.Equal(2, lista.GetProperty("totalItems").GetInt32());
        Assert.All(lista.GetProperty("items").EnumerateArray(), x => Assert.False(x.TryGetProperty("addressText", out _)));
        var byStatus = conteos.GetProperty("byStatus");
        Assert.Equal(9, byStatus.EnumerateObject().Count());
        Assert.Equal(1, byStatus.GetProperty("asignado").GetInt32());
        Assert.Equal(1, byStatus.GetProperty("cancelado").GetInt32());
        Assert.Equal(1, conteos.GetProperty("createdToday").GetInt32());
    }

    [Fact]
    public async Task AdminPlataforma_CuentaCadaTenant_ConElRolDePlataforma()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await InsertAsync(a, "buscando_tecnico");
        await InsertAsync(a, "buscando_tecnico");
        await InsertAsync(b, "pagado", "now() - interval '2 days'");
        using var http = As(Guid.NewGuid(), "admin_plataforma");

        var todos = await http.GetFromJsonAsync<JsonElement>(new Uri("/v1/platform/service-requests/summary", UriKind.Relative));
        var soloB = await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/platform/service-requests/summary?tenantId={b}", UriKind.Relative));

        var tenants = todos.GetProperty("tenants").EnumerateArray().ToDictionary(t => t.GetProperty("tenantId").GetGuid());
        Assert.Equal(2, tenants[a].GetProperty("byStatus").GetProperty("buscando_tecnico").GetInt32());
        Assert.Equal(2, tenants[a].GetProperty("createdToday").GetInt32());
        Assert.Equal(1, tenants[b].GetProperty("byStatus").GetProperty("pagado").GetInt32());
        Assert.Equal(0, tenants[b].GetProperty("createdToday").GetInt32());
        var unico = Assert.Single(soloB.GetProperty("tenants").EnumerateArray());
        Assert.Equal(b, unico.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task AdminTenant_EnLaRutaDePlataforma_403()
    {
        using var http = As(Guid.NewGuid(), "admin_tenant");

        using var response = await http.GetAsync(new Uri("/v1/platform/service-requests/summary", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("SELECT count(*) FROM service_requests", null)]
    [InlineData("SELECT description FROM service_requests LIMIT 1", "42501")]
    [InlineData("SELECT address_text FROM service_requests LIMIT 1", "42501")]
    [InlineData("SELECT client_id FROM service_requests LIMIT 1", "42501")]
    [InlineData("SELECT count(*) FROM outbox_events", "42501")]
    [InlineData("UPDATE service_requests SET status = 'cancelado'", "42501")]
    public async Task RolDePlataforma_SoloPuedeContar(string sql, string? expectedSqlState)
    {
        await InsertAsync(Guid.NewGuid(), "asignado");

        // Como lo usa el servicio: la conexión de service_request_app adopta el rol con SET LOCAL ROLE.
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE service_request_platform", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (expectedSqlState is null)
        {
            Assert.True((long)(await command.ExecuteScalarAsync())! > 0);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(expectedSqlState, ex.SqlState);
        }
    }

    [Fact]
    public async Task RolDeLaApp_NoHeredaElDePlataforma_SinTenantNoVeNada()
    {
        await InsertAsync(Guid.NewGuid(), "asignado");

        // INHERIT FALSE: sin SET ROLE, service_request_app sigue bajo RLS y sin tenant fijado no ve filas.
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM service_requests", connection);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}