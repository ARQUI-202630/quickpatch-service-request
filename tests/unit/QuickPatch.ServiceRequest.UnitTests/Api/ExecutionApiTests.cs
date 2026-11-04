using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>Iniciar, evidencias y completar (contrato <c>service-request.v1.yaml</c> 1.7.0, RF-14, RF-15).</summary>
public class ExecutionApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();

    private DomainServiceRequest Seed(string status)
    {
        var now = DateTimeOffset.UtcNow;
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, client, Guid.NewGuid(), technician, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45", status, now.AddHours(-2), now.AddHours(-1));
        var quote = Quote.Restore(
            Guid.NewGuid(), tenant, request.Id, technician, 120000m, 65000m, null, QuoteStatus.Aceptada, now.AddMinutes(-50), now.AddHours(23));
        lock (factory.Store)
        {
            factory.Store.ServiceRequests.Add(request);
            factory.Store.Quotes.Add(quote);
        }

        return request;
    }

    private HttpClient As(Guid user, string role)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return http;
    }

    private static Uri Path(Guid id, string suffix) => new($"/v1/service-requests/{id}/{suffix}", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static MultipartFormDataContent Photo(byte[] content, string field = "file", string declaredType = "image/jpeg")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(declaredType);
        form.Add(file, field, "foto.jpg");
        return form;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient http, Guid id, MultipartFormDataContent form)
    {
        using (form)
        {
            return await http.PostAsync(Path(id, "evidence"), form);
        }
    }

    [Fact]
    public async Task FlujoCompleto_Iniciar_Subir_Listar_Descargar_Completar()
    {
        var request = Seed(ServiceRequestStatus.CotizacionAceptada);
        using var asTechnician = As(technician, "tecnico");
        using var asClient = As(client, "cliente");

        using var start = await asTechnician.PostAsync(Path(request.Id, "start"), null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("en_progreso", (await Json(start)).GetProperty("status").GetString());

        using var empty = await asTechnician.PostAsync(Path(request.Id, "complete"), null);
        Assert.Equal((HttpStatusCode)422, empty.StatusCode);
        Assert.EndsWith("/problems/evidencia-requerida", (await Json(empty)).GetProperty("type").GetString());

        // El tipo declarado (jpeg) no importa: se guarda el que dice el contenido (png).
        using var upload = await UploadAsync(asTechnician, request.Id, Photo(Png));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var evidence = await Json(upload);
        var evidenceId = evidence.GetProperty("id").GetGuid();
        Assert.False(evidence.TryGetProperty("fileUrl", out _));

        using var list = await asClient.GetAsync(Path(request.Id, "evidence"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var item = Assert.Single((await Json(list)).GetProperty("items").EnumerateArray());
        Assert.Equal(evidenceId, item.GetProperty("id").GetGuid());
        Assert.Equal(["id", "uploadedAt"], item.EnumerateObject().Select(p => p.Name));

        using var file = await asClient.GetAsync(Path(request.Id, $"evidence/{evidenceId}/file"));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(EvidenceImage.Png, file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await file.Content.ReadAsByteArrayAsync());
        Assert.True(file.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromHours(1), file.Headers.CacheControl.MaxAge);

        using var complete = await asTechnician.PostAsync(Path(request.Id, "complete"), null);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var body = await Json(complete);
        Assert.Equal("completado", body.GetProperty("status").GetString());
        Assert.True(body.TryGetProperty("completedAt", out _));

        using var detail = await asClient.GetAsync(new Uri($"/v1/service-requests/{request.Id}", UriKind.Relative));
        var view = await Json(detail);
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("startedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public async Task Iniciar_FueraDeCotizacionAceptada_409()
    {
        var request = Seed(ServiceRequestStatus.Cotizado);
        using var http = As(technician, "tecnico");

        using var response = await http.PostAsync(Path(request.Id, "start"), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith("/problems/transicion-invalida", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Cliente_NoInicia_NiSube_NiCompleta_403()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        using var http = As(client, "cliente");

        using var start = await http.PostAsync(Path(request.Id, "start"), null);
        using var upload = await UploadAsync(http, request.Id, Photo(Png));
        using var complete = await http.PostAsync(Path(request.Id, "complete"), null);

        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, complete.StatusCode);
    }

    [Fact]
    public async Task OtroTecnico_404_YOtroCliente_NoVeLasFotos_404()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        using var otherTechnician = As(Guid.NewGuid(), "tecnico");
        using var otherClient = As(Guid.NewGuid(), "cliente");

        using var upload = await UploadAsync(otherTechnician, request.Id, Photo(Png));
        using var list = await otherClient.GetAsync(Path(request.Id, "evidence"));

        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }

    [Fact]
    public async Task AdminTenant_VeLasFotos_YAdminPlataforma_403()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        using var asTechnician = As(technician, "tecnico");
        using var admin = As(Guid.NewGuid(), "admin_tenant");
        using var platform = As(Guid.NewGuid(), "admin_plataforma");
        using (await UploadAsync(asTechnician, request.Id, Photo(Png)))
        {
        }

        using var list = await admin.GetAsync(Path(request.Id, "evidence"));
        using var denied = await platform.GetAsync(Path(request.Id, "evidence"));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await Json(list)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ArchivoQueNoEsImagen_400EnFile()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        using var http = As(technician, "tecnico");

        using var response = await UploadAsync(http, request.Id, Photo("no soy una foto"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("file", out _));
    }

    [Fact]
    public async Task SinCampoFile_O_SinMultipart_400EnFile()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        using var http = As(technician, "tecnico");

        using var otherField = await UploadAsync(http, request.Id, Photo(Png, field: "foto"));
        using var json = await http.PostAsync(Path(request.Id, "evidence"), new StringContent("{}", null, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, otherField.StatusCode);
        Assert.True((await Json(otherField)).GetProperty("errors").TryGetProperty("file", out _));
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
    }

    [Fact]
    public async Task MasDeDiezMegas_400SinLlegarAGarage()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        using var http = As(technician, "tecnico");
        var big = new byte[ServiceEvidence.MaxBytes + 1];
        Png.CopyTo(big, 0);

        using var response = await UploadAsync(http, request.Id, Photo(big));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        lock (factory.EvidenceStorage.Objects)
        {
            Assert.DoesNotContain(factory.EvidenceStorage.Objects.Keys, k => k.Contains(request.Id.ToString(), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task LaUndecima_409LimiteDeEvidencias()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        for (var i = 0; i < ServiceEvidence.MaxPerServiceRequest; i++)
        {
            factory.Store.Add(ServiceEvidence.Create(tenant, request.Id, technician, DateTimeOffset.UtcNow.AddMinutes(-i)));
        }

        using var http = As(technician, "tecnico");

        using var response = await UploadAsync(http, request.Id, Photo(Png));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith("/problems/limite-de-evidencias", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task DescargarEvidenciaInexistente_404()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        using var http = As(client, "cliente");

        using var response = await http.GetAsync(Path(request.Id, $"evidence/{Guid.NewGuid()}/file"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}