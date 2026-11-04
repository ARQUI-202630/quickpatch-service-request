using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Npgsql;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-475 contra PostgreSQL real: la cancelación corre como <c>service_request_app</c> bajo RLS, bloquea la fila, deja
/// los dos eventos en el Outbox y, si cancela el <c>admin_tenant</c>, una fila en <c>audit_logs</c> que la aplicación no
/// puede modificar (RN-A1).
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class CancelFlowTests(ServiceRequestEnvironment env)
{
    private async Task<Guid> InsertAsync(Guid tenant, Guid client, string status)
    {
        var id = Guid.NewGuid();
        await env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, description, location, address_text, status, created_at) " +
            $"VALUES ('{id}', '{tenant}', '{client}', '{Guid.NewGuid()}', 'Fuga de agua en la cocina', " +
            $"ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', '{status}', now());");
        return id;
    }

    private HttpClient As(Guid user, Guid tenant, string role)
    {
        var http = env.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return http;
    }

    private static Uri CancelUri(Guid id) => new($"/v1/service-requests/{id}/cancel", UriKind.Relative);

    [Fact]
    public async Task Cliente_Cancela_GuardaQuienYPorQue_YDejaLosDosEventos()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, "asignado");
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(CancelUri(id), new { reason = "Ya no necesito el servicio." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cancelado", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal("cliente", await env.ScalarAdminAsync<string>("SELECT cancelled_by_type FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(client, await env.ScalarAdminAsync<Guid>("SELECT cancelled_by FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal("Ya no necesito el servicio.", await env.ScalarAdminAsync<string>("SELECT cancellation_reason FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.cancelled'", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.status-changed'", ("id", id)));
        Assert.Equal(0L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM audit_logs WHERE target_id = @id", ("id", id)));
    }

    [Fact]
    public async Task AdminTenant_Cancela_YQuedaEnAuditoria_QueLaAppNoPuedeModificar()
    {
        var tenant = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var id = await InsertAsync(tenant, Guid.NewGuid(), "en_espera");
        using var http = As(admin, tenant, "admin_tenant");

        using var response = await http.PostAsJsonAsync(CancelUri(id), new { reason = "Cliente sin respuesta." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(admin, await env.ScalarAdminAsync<Guid>(
            "SELECT actor_user_id FROM audit_logs WHERE target_id = @id AND action = 'cancelar_solicitud_admin'", ("id", id)));

        // RN-A1: con las credenciales de la aplicación, la auditoría no se modifica ni se borra.
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_tenant', '{tenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var update = new NpgsqlCommand("UPDATE audit_logs SET action = 'otra'", connection, transaction);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task OtroTenant_404_YLaAuditoriaDeOtroTenantNoSeVe()
    {
        var tenant = Guid.NewGuid();
        var id = await InsertAsync(tenant, Guid.NewGuid(), "buscando_tecnico");
        using var admin = As(Guid.NewGuid(), tenant, "admin_tenant");
        using var ajeno = As(Guid.NewGuid(), Guid.NewGuid(), "admin_tenant");

        using var rechazo = await ajeno.PostAsJsonAsync(CancelUri(id), new { reason = "Intento de otro tenant." });
        using var ok = await admin.PostAsJsonAsync(CancelUri(id), new { reason = "Cancelada por el admin." });

        Assert.Equal(HttpStatusCode.NotFound, rechazo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // RLS sobre audit_logs: bajo otro tenant la fila no existe.
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_tenant', '{Guid.NewGuid()}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM audit_logs WHERE target_id = '{id}'", connection, transaction);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task YaIniciada_409_SinCambios()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, "en_progreso");
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(CancelUri(id), new { reason = "Ya no necesito el servicio." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("en_progreso", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(0L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM outbox_events WHERE aggregate_id = @id", ("id", id)));
    }

    [Fact]
    public async Task CancelacionEspera_SiOtraTransaccionTieneLaFila()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, "buscando_tecnico");

        // Otra transacción (como el consumidor de Matching) tiene la fila bloqueada.
        await using var holder = new NpgsqlConnection(env.AdminConnectionString);
        await holder.OpenAsync();
        await using var lockTx = await holder.BeginTransactionAsync();
        await using (var select = new NpgsqlCommand($"SELECT id FROM service_requests WHERE id = '{id}' FOR UPDATE", holder, lockTx))
        {
            await select.ExecuteScalarAsync();
        }

        using var http = As(client, tenant, "cliente");
        var cancel = http.PostAsJsonAsync(CancelUri(id), new { reason = "Ya no necesito el servicio." });

        await Task.Delay(1000);
        Assert.False(cancel.IsCompleted);

        await lockTx.CommitAsync();
        using var response = await cancel;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}