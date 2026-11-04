using System.Text.Json;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Ratings;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Calificación del servicio pagado (RF-12, RN-R1 a RN-R3; DD 8.3.11, ADR-027).</summary>
public class RateServiceRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();

    private DomainServiceRequest Seed(string status, Guid? tenantId = null, Guid? clientId = null)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenantId ?? tenant, clientId ?? client, Guid.NewGuid(), technician, "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", status, Now.AddHours(-3), Now.AddHours(-2));
        store.ServiceRequests.Add(request);
        return request;
    }

    private Task<RateServiceRequestResult> Rate(Guid id, int? score = 5, string? comment = "Puntual y ordenado.", Guid? user = null) =>
        new RateServiceRequestHandler(store, store, store, store, new FixedClock(Now))
            .HandleAsync(new RateServiceRequestCommand(tenant, user ?? client, id, score, comment, Guid.NewGuid()), CancellationToken.None);

    [Fact]
    public async Task DuenioDeSolicitudPagada_GuardaLaCalificacion_YDejaElEvento()
    {
        var request = Seed(ServiceRequestStatus.Pagado);

        var result = await Rate(request.Id, 4, "  Puntual y ordenado.  ");

        var rating = Assert.IsType<RateServiceRequestResult.Rated>(result).Rating;
        Assert.Equal(4, rating.Score);
        Assert.Equal("Puntual y ordenado.", rating.Comment);
        Assert.Equal(technician, rating.TechnicianId);
        Assert.Equal(tenant, rating.TenantId);
        Assert.Equal(Now, rating.CreatedAt);
        Assert.Single(store.Ratings);
        Assert.Equal([EventTypes.ServiceRated], store.Outbox.Select(m => m.EventType));
        Assert.Equal(ServiceRequestStatus.Pagado, request.Status);
    }

    [Fact]
    public async Task EventoRated_LlevaLosCamposDelDd_SinElComentario()
    {
        var request = Seed(ServiceRequestStatus.Pagado);

        await Rate(request.Id, 3, "No viaja en el evento.");

        var root = JsonDocument.Parse(store.Outbox[0].Payload).RootElement;
        Assert.Equal("service-request.rated", root.GetProperty("eventType").GetString());
        Assert.Equal(tenant, root.GetProperty("tenantId").GetGuid());
        var data = root.GetProperty("data");
        Assert.Equal(
            ["ratedAt", "score", "serviceRequestId", "technicianId"],
            data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(request.Id, data.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal(technician, data.GetProperty("technicianId").GetGuid());
        Assert.Equal(3, data.GetProperty("score").GetInt32());
        Assert.DoesNotContain("No viaja", store.Outbox[0].Payload, StringComparison.Ordinal);
        Assert.Equal(request.Id, store.Outbox[0].AggregateId);
    }

    [Fact]
    public async Task SinComentario_LoGuardaNulo()
    {
        var request = Seed(ServiceRequestStatus.Pagado);

        var result = await Rate(request.Id, 5, "   ");

        Assert.Null(Assert.IsType<RateServiceRequestResult.Rated>(result).Rating.Comment);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.EnProgreso)]
    [InlineData(ServiceRequestStatus.Completado)]
    [InlineData(ServiceRequestStatus.Cancelado)]
    [InlineData(ServiceRequestStatus.Asignado)]
    public async Task SinPagar_EstadoInvalido_SinFilaNiEvento(string status)
    {
        var request = Seed(status);

        var result = await Rate(request.Id);

        Assert.Equal(status, Assert.IsType<RateServiceRequestResult.InvalidState>(result).Status);
        Assert.Empty(store.Ratings);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task SegundaCalificacion_YaCalificada_UnaSolaFila()
    {
        var request = Seed(ServiceRequestStatus.Pagado);
        await Rate(request.Id, 5);

        var result = await Rate(request.Id, 1);

        Assert.IsType<RateServiceRequestResult.AlreadyRated>(result);
        Assert.Equal(5, Assert.Single(store.Ratings).Score);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task OtroUsuarioDelTenant_NoEsElDuenio()
    {
        var request = Seed(ServiceRequestStatus.Pagado);

        var result = await Rate(request.Id, user: Guid.NewGuid());

        Assert.IsType<RateServiceRequestResult.NotOwner>(result);
        Assert.Empty(store.Ratings);
    }

    [Fact]
    public async Task SolicitudDeOtroTenant_NoEncontrada()
    {
        var request = Seed(ServiceRequestStatus.Pagado, tenantId: Guid.NewGuid());

        var result = await Rate(request.Id);

        Assert.IsType<RateServiceRequestResult.NotFound>(result);
        Assert.Empty(store.Outbox);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(null)]
    public async Task PuntajeFueraDeRango_ErrorDelCampo_SinTransaccion(int? score)
    {
        var request = Seed(ServiceRequestStatus.Pagado);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Rate(request.Id, score));

        Assert.Contains("score", ex.Errors.Keys);
        Assert.Equal(0, store.Transactions);
    }

    [Fact]
    public async Task ComentarioDeMasDe500_ErrorDelCampo()
    {
        var request = Seed(ServiceRequestStatus.Pagado);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Rate(request.Id, 5, new string('a', Rating.MaxCommentLength + 1)));

        Assert.Contains("comment", ex.Errors.Keys);
    }

    [Fact]
    public async Task ComentarioDe500_SeAcepta()
    {
        var request = Seed(ServiceRequestStatus.Pagado);

        var result = await Rate(request.Id, 5, new string('a', Rating.MaxCommentLength));

        Assert.IsType<RateServiceRequestResult.Rated>(result);
    }
}