using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Npgsql;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-85 contra PostgreSQL real (RF-12, DD 5.9, ADR-027): la calificación corre como <c>service_request_app</c> bajo
/// RLS, guarda la fila y <c>service-request.rated</c> en la misma transacción, y la base respalda RN-R1 y RN-R2 aunque el
/// caso de uso fallara.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class RatingFlowTests(ServiceRequestEnvironment env)
{
    private async Task<Guid> InsertAsync(Guid tenant, Guid client, Guid technician, string status)
    {
        var id = Guid.NewGuid();
        await env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, technician_id, description, location, address_text, status, created_at) " +
            $"VALUES ('{id}', '{tenant}', '{client}', '{Guid.NewGuid()}', '{technician}', 'Fuga de agua en la cocina', " +
            $"ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', '{status}', now());");
        return id;
    }

    private HttpClient As(Guid user, Guid tenant, string role)
    {
        var http = env.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return http;
    }

    private static Uri RatingUri(Guid id) => new($"/v1/service-requests/{id}/rating", UriKind.Relative);

    /// <summary>Conexión con las credenciales de la aplicación y el tenant fijado, como el TenantUnitOfWork.</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> AppSessionAsync(Guid tenant)
    {
        var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var set = new NpgsqlCommand($"SELECT set_config('app.current_tenant', '{tenant}', true)", connection, transaction);
        await set.ExecuteNonQueryAsync();
        return (connection, transaction);
    }

    [Fact]
    public async Task Cliente_CalificaLaSolicitudPagada_FilaYEventoJuntos_SinCambiarElEstado()
    {
        var (tenant, client, technician) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, technician, "pagado");
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(RatingUri(id), new { score = 4, comment = "Puntual y ordenado." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(4, await env.ScalarAdminAsync<int>("SELECT score FROM ratings WHERE service_request_id = @id", ("id", id)));
        Assert.Equal(technician, await env.ScalarAdminAsync<Guid>("SELECT technician_id FROM ratings WHERE service_request_id = @id", ("id", id)));
        Assert.Equal(tenant, await env.ScalarAdminAsync<Guid>("SELECT tenant_id FROM ratings WHERE service_request_id = @id", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.rated'", ("id", id)));
        Assert.Equal("pagado", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
    }

    [Fact]
    public async Task Repetida_409_UnaSolaFilaYUnSoloEvento()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, Guid.NewGuid(), "pagado");
        using var http = As(client, tenant, "cliente");

        using var first = await http.PostAsJsonAsync(RatingUri(id), new { score = 5 });
        using var second = await http.PostAsJsonAsync(RatingUri(id), new { score = 1 });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM ratings WHERE service_request_id = @id", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.rated'", ("id", id)));
    }

    [Fact]
    public async Task SinPagar_409_SinFila()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, Guid.NewGuid(), "completado");
        using var http = As(client, tenant, "cliente");

        using var response = await http.PostAsJsonAsync(RatingUri(id), new { score = 5 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await env.ScalarAdminAsync<long>("SELECT count(*) FROM ratings WHERE service_request_id = @id", ("id", id)));
    }

    [Fact]
    public async Task OtroTenant_404_YLaCalificacionNoSeVeBajoOtroTenant()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, Guid.NewGuid(), "pagado");
        using var ajeno = As(client, Guid.NewGuid(), "cliente");
        using var duenio = As(client, tenant, "cliente");

        using var rechazo = await ajeno.PostAsJsonAsync(RatingUri(id), new { score = 1 });
        using var ok = await duenio.PostAsJsonAsync(RatingUri(id), new { score = 5 });

        Assert.Equal(HttpStatusCode.NotFound, rechazo.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var (connection, transaction) = await AppSessionAsync(Guid.NewGuid());
        await using (connection)
        await using (transaction)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM ratings WHERE service_request_id = '{id}'", connection, transaction);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task LaAplicacionNoModificaCalificaciones_YLaBaseRespaldaRnR1YRnR2()
    {
        var (tenant, client, technician) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var id = await InsertAsync(tenant, client, technician, "pagado");
        using var http = As(client, tenant, "cliente");
        (await http.PostAsJsonAsync(RatingUri(id), new { score = 5 })).Dispose();

        var (connection, transaction) = await AppSessionAsync(tenant);
        await using (connection)
        await using (transaction)
        {
            // Una calificación no se edita: el rol de la aplicación no tiene UPDATE.
            await using var update = new NpgsqlCommand("UPDATE ratings SET score = 1", connection, transaction);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
        }

        // RN-R2 en la base: una segunda fila para la misma solicitud viola el índice único.
        var duplicate = Assert.IsType<PostgresException>(await Record.ExceptionAsync(() => env.ExecuteAdminAsync(
            "INSERT INTO ratings (id, tenant_id, service_request_id, technician_id, score, created_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{tenant}', '{id}', '{technician}', 3, now());")));
        Assert.Equal("23505", duplicate.SqlState);

        // RN-R1 en la base: el CHECK rechaza un puntaje fuera de 1 a 5.
        var other = await InsertAsync(tenant, client, technician, "pagado");
        var outOfRange = Assert.IsType<PostgresException>(await Record.ExceptionAsync(() => env.ExecuteAdminAsync(
            "INSERT INTO ratings (id, tenant_id, service_request_id, technician_id, score, created_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{tenant}', '{other}', '{technician}', 6, now());")));
        Assert.Equal("23514", outOfRange.SqlState);
    }
}