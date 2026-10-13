using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Matching;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

public class MatchingStatusHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly DomainServiceRequest request;

    public MatchingStatusHandlerTests()
    {
        request = DomainServiceRequest.Create(tenant, Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", Now);
        store.ServiceRequests.Add(request);
    }

    private MatchingStatusHandler Handler() =>
        new(store, store, store, new FixedClock(Now), NullLogger<MatchingStatusHandler>.Instance);

    private EventEnvelope<NoTechnicianAvailableData> SinTecnicos(DateTimeOffset occurredAt, Guid? eventId = null, Guid? requestId = null) =>
        new(eventId ?? Guid.NewGuid(), EventTypes.NoTechnicianAvailable, 1, occurredAt, Guid.NewGuid(), tenant,
            "matching-service", new NoTechnicianAvailableData(requestId ?? request.Id, request.ClientId, 0));

    private EventEnvelope<SearchResumedData> Reanudada(DateTimeOffset occurredAt) =>
        new(Guid.NewGuid(), EventTypes.SearchResumed, 1, occurredAt, Guid.NewGuid(), tenant, "matching-service",
            new SearchResumedData(request.Id, request.ClientId, "disponibilidad"));

    [Fact]
    public async Task SinTecnicos_PasaAEnEsperaBajoElTenantDelEventoYLoRegistra()
    {
        var evento = SinTecnicos(Now.AddSeconds(1));

        var outcome = await Handler().HandleNoTechnicianAvailableAsync(evento, CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.Applied, outcome);
        Assert.Equal(ServiceRequestStatus.EnEspera, request.Status);
        Assert.Equal(Now.AddSeconds(1), request.MatchingUpdatedAt);
        var procesado = Assert.Single(store.ProcessedEvents);
        Assert.Equal((evento.EventId, tenant, EventTypes.NoTechnicianAvailable), procesado);
        Assert.Equal(1, store.Transactions);
    }

    [Fact]
    public async Task Reanudada_VuelveABuscandoTecnico()
    {
        await Handler().HandleNoTechnicianAvailableAsync(SinTecnicos(Now.AddSeconds(1)), CancellationToken.None);

        var outcome = await Handler().HandleSearchResumedAsync(Reanudada(Now.AddMinutes(5)), CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.Applied, outcome);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
    }

    [Fact]
    public async Task EventoRepetido_SeAplicaUnaSolaVez()
    {
        var evento = SinTecnicos(Now.AddSeconds(1));
        await Handler().HandleNoTechnicianAvailableAsync(evento, CancellationToken.None);

        var outcome = await Handler().HandleNoTechnicianAvailableAsync(evento, CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.Duplicate, outcome);
        Assert.Single(store.ProcessedEvents);
    }

    [Fact]
    public async Task EventosEnDesorden_ElMasViejoNoDejaLaSolicitudEnEspera()
    {
        // Matching publicó primero no-technician-available y después search-resumed, pero llegan al revés.
        var reanudada = await Handler().HandleSearchResumedAsync(Reanudada(Now.AddMinutes(5)), CancellationToken.None);
        var sinTecnicos = await Handler().HandleNoTechnicianAvailableAsync(SinTecnicos(Now.AddSeconds(1)), CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.Ignored, reanudada);
        Assert.Equal(MatchingEventHandlingOutcome.Stale, sinTecnicos);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
        Assert.Equal(2, store.ProcessedEvents.Count);
    }

    [Fact]
    public async Task SolicitudQueNoExisteEnElTenant_SeRegistraYSeDescarta()
    {
        var outcome = await Handler().HandleNoTechnicianAvailableAsync(
            SinTecnicos(Now.AddSeconds(1), requestId: Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.NotFound, outcome);
        Assert.Single(store.ProcessedEvents);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
    }

    [Fact]
    public async Task EventoNulo_LanzaExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleNoTechnicianAvailableAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleSearchResumedAsync(null!, CancellationToken.None));
    }
}