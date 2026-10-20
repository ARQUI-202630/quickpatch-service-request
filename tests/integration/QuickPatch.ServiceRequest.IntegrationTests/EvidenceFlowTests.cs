using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Amazon.S3;

using Npgsql;

using QuickPatch.ServiceRequest.Infrastructure.Options;
using QuickPatch.ServiceRequest.Infrastructure.Storage;
using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-94 contra PostgreSQL, Kafka y Garage reales: iniciar, subir una foto al bucket de evidencias, listarla, descargarla y
/// completar, con <c>service_request_app</c> bajo RLS y la llave de Garage que solo ve ese bucket.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class EvidenceFlowTests(ServiceRequestEnvironment env)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0xFF, 0xD9];

    private async Task<(Guid Id, Guid Tenant, Guid Client, Guid Technician)> InsertAcceptedAsync()
    {
        var (id, tenant, client, technician) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, technician_id, description, location, address_text, " +
            "status, created_at, assigned_at) " +
            $"VALUES ('{id}', '{tenant}', '{client}', '{Guid.NewGuid()}', '{technician}', 'Fuga de agua en la cocina', " +
            $"ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', 'cotizacion_aceptada', now(), now());" +
            "INSERT INTO quotes (id, tenant_id, service_request_id, technician_id, labor_amount, materials_amount, status, created_at, " +
            $"expires_at, responded_at) VALUES (gen_random_uuid(), '{tenant}', '{id}', '{technician}', 120000, 65000, 'aceptada', " +
            "now() - interval '1 hour', now() + interval '23 hours', now());");
        return (id, tenant, client, technician);
    }

    private HttpClient As(Guid user, Guid tenant, string role)
    {
        var http = env.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return http;
    }

    private static Uri Path(Guid id, string suffix) => new($"/v1/service-requests/{id}/{suffix}", UriKind.Relative);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient http, Guid id)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "foto.jpg");
        return await http.PostAsync(Path(id, "evidence"), form);
    }

    [Fact]
    public async Task Iniciar_Subir_Descargar_Completar_ConGarageReal()
    {
        var (id, tenant, client, technician) = await InsertAcceptedAsync();
        using var asTechnician = As(technician, tenant, "tecnico");
        using var asClient = As(client, tenant, "cliente");

        using var start = await asTechnician.PostAsync(Path(id, "start"), null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        using var upload = await UploadAsync(asTechnician, id);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var evidenceId = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // La fila guarda la clave del objeto, y el objeto está en el bucket de evidencias.
        var key = await env.ScalarAdminAsync<string>("SELECT file_url FROM service_evidence WHERE id = @id", ("id", evidenceId));
        Assert.Equal($"{tenant}/{id}/{evidenceId}", key);
        using (var s3 = GarageEvidenceStorage.CreateClient(Options()))
        {
            using var stored = await s3.GetObjectAsync(ServiceRequestEnvironment.EvidenceBucket, key);
            Assert.Equal("image/jpeg", stored.Headers.ContentType);
        }

        using var file = await asClient.GetAsync(Path(id, $"evidence/{evidenceId}/file"));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Jpeg, await file.Content.ReadAsByteArrayAsync());

        using var complete = await asTechnician.PostAsync(Path(id, "complete"), null);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("completado", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
        Assert.True(await env.ScalarAdminAsync<bool>(
            "SELECT started_at IS NOT NULL AND completed_at IS NOT NULL FROM service_requests WHERE id = @id", ("id", id)));

        var payload = await env.ScalarAdminAsync<string>(
            "SELECT payload::text FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.completed'", ("id", id));
        var data = JsonDocument.Parse(payload!).RootElement.GetProperty("data");
        Assert.Equal("185000.00", data.GetProperty("totalAmount").GetString());
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.started'", ("id", id)));
    }

    [Fact]
    public async Task Completar_SinEvidencia_422_YLaSolicitudSigueEnProgreso()
    {
        var (id, tenant, _, technician) = await InsertAcceptedAsync();
        using var asTechnician = As(technician, tenant, "tecnico");
        using (await asTechnician.PostAsync(Path(id, "start"), null))
        {
        }

        using var complete = await asTechnician.PostAsync(Path(id, "complete"), null);

        Assert.Equal((HttpStatusCode)422, complete.StatusCode);
        Assert.Equal("en_progreso", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));
    }

    [Fact]
    public async Task OtroTenant_NoVeNiSubeEvidencias_YRlsOcultaLaFila()
    {
        var (id, tenant, client, technician) = await InsertAcceptedAsync();
        using var asTechnician = As(technician, tenant, "tecnico");
        using (await asTechnician.PostAsync(Path(id, "start"), null))
        {
        }

        using var upload = await UploadAsync(asTechnician, id);
        var evidenceId = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // Mismo cliente y mismo técnico, con el token de otro tenant; y un admin_tenant de otro tenant.
        var otherTenant = Guid.NewGuid();
        using var otherClient = As(client, otherTenant, "cliente");
        using var otherTechnician = As(technician, otherTenant, "tecnico");
        using var otherAdmin = As(Guid.NewGuid(), otherTenant, "admin_tenant");

        using var list = await otherClient.GetAsync(Path(id, "evidence"));
        using var file = await otherAdmin.GetAsync(Path(id, $"evidence/{evidenceId}/file"));
        using var foreignUpload = await UploadAsync(otherTechnician, id);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignUpload.StatusCode);
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM service_evidence WHERE service_request_id = @id", ("id", id)));

        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_tenant', '{otherTenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM service_evidence WHERE id = '{evidenceId}'", connection, transaction);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task LaAplicacion_NoPuedeCambiarNiBorrarEvidencias()
    {
        var (id, tenant, _, technician) = await InsertAcceptedAsync();
        using var asTechnician = As(technician, tenant, "tecnico");
        using (await asTechnician.PostAsync(Path(id, "start"), null))
        {
        }

        using (await UploadAsync(asTechnician, id))
        {
        }

        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     $"DELETE FROM service_evidence WHERE service_request_id = '{id}'",
                     $"UPDATE service_evidence SET file_url = 'otra' WHERE service_request_id = '{id}'",
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // permiso denegado
        }
    }

    private EvidenceStorageOptions Options() => new()
    {
        Endpoint = env.GarageEndpoint,
        AccessKey = ServiceRequestEnvironment.GarageKeyId,
        SecretKey = ServiceRequestEnvironment.GarageSecret,
    };
}