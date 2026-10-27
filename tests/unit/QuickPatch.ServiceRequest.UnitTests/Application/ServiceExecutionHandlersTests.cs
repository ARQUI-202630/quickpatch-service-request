using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Execution;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Iniciar, subir evidencia y completar (RF-14, RF-15, RN-SR3, RN-SR6, RN-E1 a RN-E4; contrato 1.7.0).</summary>
public class ServiceExecutionHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    /// <summary>Cabecera mínima de un JPEG.</summary>
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private readonly InMemoryStore store = new();
    private readonly InMemoryEvidenceStorage storage = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();

    private DomainServiceRequest Seed(string status, Guid? tenantId = null, Guid? companyId = null)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenantId ?? tenant, client, Guid.NewGuid(), technician, "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", status, Now.AddHours(-3), Now.AddHours(-2), companyId);
        store.ServiceRequests.Add(request);
        return request;
    }

    private Quote SeedAcceptedQuote(DomainServiceRequest request)
    {
        var quote = Quote.Restore(
            Guid.NewGuid(), request.TenantId, request.Id, technician, 120000m, 65000m, null, QuoteStatus.Aceptada,
            Now.AddHours(-1), Now.AddHours(23));
        store.Quotes.Add(quote);
        return quote;
    }

    private void SeedEvidence(DomainServiceRequest request, int count)
    {
        for (var i = 0; i < count; i++)
        {
            store.Add(ServiceEvidence.Create(request.TenantId, request.Id, technician, Now.AddMinutes(-30 + i)));
        }
    }

    private Task<ServiceStepResult> Start(Guid id, Guid? user = null) =>
        new StartServiceHandler(store, store, store, new FixedClock(Now))
            .HandleAsync(new ServiceStepCommand(tenant, user ?? technician, id, Guid.NewGuid()), CancellationToken.None);

    private Task<ServiceStepResult> Complete(Guid id, Guid? user = null) =>
        new CompleteServiceHandler(store, store, store, store, store, new FixedClock(Now))
            .HandleAsync(new ServiceStepCommand(tenant, user ?? technician, id, Guid.NewGuid()), CancellationToken.None);

    private Task<UploadEvidenceResult> Upload(Guid id, byte[]? content = null, Guid? user = null) =>
        new UploadEvidenceHandler(store, store, store, storage, new FixedClock(Now), NullLogger<UploadEvidenceHandler>.Instance)
            .HandleAsync(new UploadEvidenceCommand(tenant, user ?? technician, id, content ?? Jpeg), CancellationToken.None);

    private Task<IReadOnlyList<ServiceEvidence>?> List(Guid id, Guid user, EvidenceViewer viewer, Guid? tenantId = null) =>
        new ListEvidenceHandler(store, store, store)
            .HandleAsync(new EvidenceQuery(tenantId ?? tenant, user, viewer, id), CancellationToken.None);

    private static JsonElement Data(OutboxMessage m) => JsonDocument.Parse(m.Payload).RootElement.GetProperty("data");

    [Fact]
    public async Task Iniciar_DesdeCotizacionAceptada_EnProgreso_ConStartedYStatusChanged()
    {
        var request = Seed(ServiceRequestStatus.CotizacionAceptada);

        Assert.IsType<ServiceStepResult.Done>(await Start(request.Id));

        Assert.Equal(ServiceRequestStatus.EnProgreso, request.Status);
        Assert.Equal(Now, request.StartedAt);
        Assert.Equal([EventTypes.ServiceStarted, EventTypes.ServiceRequestStatusChanged], store.Outbox.Select(m => m.EventType));
        var started = Data(store.Outbox[0]);
        Assert.Equal(technician, started.GetProperty("technicianId").GetGuid());
        Assert.Equal(client, started.GetProperty("clientId").GetGuid());
        var changed = Data(store.Outbox[1]);
        Assert.Equal("cotizacion_aceptada", changed.GetProperty("previousStatus").GetString());
        Assert.Equal("en_progreso", changed.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Asignado)]
    [InlineData(ServiceRequestStatus.Cotizado)]
    [InlineData(ServiceRequestStatus.EnProgreso)]
    [InlineData(ServiceRequestStatus.Cancelado)]
    public async Task Iniciar_FueraDeCotizacionAceptada_TransicionInvalida_SinEventos(string status)
    {
        var request = Seed(status);

        var result = Assert.IsType<ServiceStepResult.InvalidTransition>(await Start(request.Id));

        Assert.Equal(status, result.Status);
        Assert.Equal(status, request.Status);
        Assert.Null(request.StartedAt);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task OtroTecnico_NoInicia_NiSube_NiCompleta_NotFound()
    {
        var request = Seed(ServiceRequestStatus.CotizacionAceptada);
        var other = Guid.NewGuid();

        Assert.IsType<ServiceStepResult.NotFound>(await Start(request.Id, other));
        Assert.IsType<UploadEvidenceResult.NotFound>(await Upload(request.Id, user: other));
        Assert.IsType<ServiceStepResult.NotFound>(await Complete(request.Id, other));
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task SolicitudDeOtroTenant_NoSeVe_NotFound()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso, Guid.NewGuid());

        Assert.IsType<UploadEvidenceResult.NotFound>(await Upload(request.Id));
        Assert.IsType<ServiceStepResult.NotFound>(await Complete(request.Id));
        Assert.Null(await List(request.Id, Guid.NewGuid(), EvidenceViewer.AdminTenant));
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task Subir_EnProgreso_GuardaArchivoYFila_ConClaveDelServicio()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);

        var uploaded = Assert.IsType<UploadEvidenceResult.Uploaded>(await Upload(request.Id)).Evidence;

        var row = Assert.Single(store.Evidence);
        Assert.Equal(uploaded.Id, row.Id);
        Assert.Equal(technician, row.TechnicianId);
        Assert.Equal($"{tenant}/{request.Id}/{row.Id}", row.FileUrl);
        Assert.Equal(EvidenceImage.Jpeg, storage.Objects[row.FileUrl].ContentType);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task Subir_FueraDeEnProgreso_TransicionInvalida_SinTocarGarage()
    {
        var request = Seed(ServiceRequestStatus.CotizacionAceptada);

        Assert.IsType<UploadEvidenceResult.InvalidTransition>(await Upload(request.Id));

        Assert.Empty(storage.Objects);
        Assert.Empty(store.Evidence);
    }

    [Fact]
    public async Task Subir_LaUndecima_LimiteDeEvidencias()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        SeedEvidence(request, ServiceEvidence.MaxPerServiceRequest);

        Assert.IsType<UploadEvidenceResult.LimitReached>(await Upload(request.Id));

        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task Subir_ArchivoQueNoEsImagen_ErrorDelCampoFile_SinTocarGarage()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Upload(request.Id, "%PDF-1.7 no es foto"u8.ToArray()));

        Assert.True(ex.Errors.ContainsKey("file"));
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task Subir_SiCompletanEnParalelo_BorraElArchivoHuerfano()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        storage.AfterPut = () => request.TryComplete(Now);

        Assert.IsType<UploadEvidenceResult.InvalidTransition>(await Upload(request.Id));

        Assert.Empty(store.Evidence);
        Assert.Empty(storage.Objects);
        Assert.Single(storage.Deleted);
    }

    [Fact]
    public async Task Subir_SiGarageFalla_NoGuardaFila()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        storage.FailPut = true;

        await Assert.ThrowsAsync<IOException>(() => Upload(request.Id));

        Assert.Empty(store.Evidence);
    }

    [Fact]
    public async Task Completar_SinEvidencia_EvidenciaRequerida_SigueEnProgreso()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        SeedAcceptedQuote(request);

        Assert.IsType<ServiceStepResult.EvidenceRequired>(await Complete(request.Id));

        Assert.Equal(ServiceRequestStatus.EnProgreso, request.Status);
        Assert.Null(request.CompletedAt);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task Completar_ConEvidencia_Completado_ConElTotalDeLaAceptada()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        var quote = SeedAcceptedQuote(request);
        SeedEvidence(request, 1);

        Assert.IsType<ServiceStepResult.Done>(await Complete(request.Id));

        Assert.Equal(ServiceRequestStatus.Completado, request.Status);
        Assert.Equal(Now, request.CompletedAt);
        Assert.Equal([EventTypes.ServiceCompleted, EventTypes.ServiceRequestStatusChanged], store.Outbox.Select(m => m.EventType));
        var completed = Data(store.Outbox[0]);
        Assert.Equal(quote.Id, completed.GetProperty("quoteId").GetGuid());
        Assert.Equal("185000.00", completed.GetProperty("totalAmount").GetString());
        Assert.Equal(technician, completed.GetProperty("technicianId").GetGuid());
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("companyId").ValueKind);
    }

    [Fact]
    public async Task Completar_SolicitudDeEmpresa_EventoLlevaLaEmpresa()
    {
        // Payments deja pagar a cualquier empresa_contacto de la empresa solo si el evento trae la empresa (RN-P6).
        var company = Guid.NewGuid();
        var request = Seed(ServiceRequestStatus.EnProgreso, companyId: company);
        SeedAcceptedQuote(request);
        SeedEvidence(request, 1);

        Assert.IsType<ServiceStepResult.Done>(await Complete(request.Id));

        var completed = Data(store.Outbox[0]);
        Assert.Equal(company, completed.GetProperty("companyId").GetGuid());
        Assert.Equal(client, completed.GetProperty("clientId").GetGuid());
    }

    [Theory]
    [InlineData(ServiceRequestStatus.CotizacionAceptada)]
    [InlineData(ServiceRequestStatus.Completado)]
    public async Task Completar_FueraDeEnProgreso_TransicionInvalida(string status)
    {
        var request = Seed(status);
        SeedEvidence(request, 1);

        Assert.IsType<ServiceStepResult.InvalidTransition>(await Complete(request.Id));
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task Listar_ClienteDueno_TecnicoAsignado_YAdminTenant_VenLasFotos_OtroClienteNo()
    {
        var request = Seed(ServiceRequestStatus.Completado);
        SeedEvidence(request, 2);

        Assert.Equal(2, (await List(request.Id, client, EvidenceViewer.Client))!.Count);
        Assert.Equal(2, (await List(request.Id, technician, EvidenceViewer.Technician))!.Count);
        Assert.Equal(2, (await List(request.Id, Guid.NewGuid(), EvidenceViewer.AdminTenant))!.Count);
        Assert.Null(await List(request.Id, Guid.NewGuid(), EvidenceViewer.Client));
        Assert.Null(await List(request.Id, Guid.NewGuid(), EvidenceViewer.Technician));
    }

    [Fact]
    public async Task Descargar_DevuelveElArchivo_YUnaEvidenciaDeOtraSolicitudNo()
    {
        var request = Seed(ServiceRequestStatus.EnProgreso);
        var other = Seed(ServiceRequestStatus.EnProgreso);
        var mine = Assert.IsType<UploadEvidenceResult.Uploaded>(await Upload(request.Id)).Evidence;
        var foreign = Assert.IsType<UploadEvidenceResult.Uploaded>(await Upload(other.Id)).Evidence;
        var download = new DownloadEvidenceHandler(new ListEvidenceHandler(store, store, store), storage);
        var query = new EvidenceQuery(tenant, client, EvidenceViewer.Client, request.Id);

        var file = await download.HandleAsync(query, mine.Id, CancellationToken.None);

        Assert.NotNull(file);
        Assert.Equal(EvidenceImage.Jpeg, file.ContentType);
        await file.Content.DisposeAsync();
        Assert.Null(await download.HandleAsync(query, foreign.Id, CancellationToken.None));
    }
}