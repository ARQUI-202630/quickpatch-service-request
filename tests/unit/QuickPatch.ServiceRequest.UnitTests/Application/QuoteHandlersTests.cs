using System.Text.Json;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Quotes;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Cotización de punta a punta en la capa de aplicación (RF-34, RF-35, RN-Q1 a RN-Q7; contrato 1.6.0).</summary>
public class QuoteHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();

    private DomainServiceRequest Seed(string status = ServiceRequestStatus.Asignado, Guid? tenantId = null)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenantId ?? tenant, client, Guid.NewGuid(), technician, "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", status, Now.AddHours(-2), Now.AddHours(-1));
        store.ServiceRequests.Add(request);
        return request;
    }

    private Task<IssueQuoteResult> Issue(Guid id, Guid? user = null, string labor = "120000.00", DateTimeOffset? at = null) =>
        new IssueQuoteHandler(store, store, store, store, new QuoteSettings(Ttl), new FixedClock(at ?? Now))
            .HandleAsync(new IssueQuoteCommand(tenant, user ?? technician, id, labor, "65000.00", "Cambio de sifón.", Guid.NewGuid()),
                CancellationToken.None);

    private Task<RespondQuoteResult> Respond(Guid id, Guid quoteId, bool accept, string? reason = null, Guid? user = null, DateTimeOffset? at = null) =>
        new RespondQuoteHandler(store, store, store, store, new FixedClock(at ?? Now.AddMinutes(10)))
            .HandleAsync(new RespondQuoteCommand(tenant, user ?? client, id, quoteId, accept, reason, Guid.NewGuid()), CancellationToken.None);

    private async Task<Quote> IssueOk(Guid id, DateTimeOffset? at = null) =>
        Assert.IsType<IssueQuoteResult.Issued>(await Issue(id, at: at)).Quote.Quote;

    private static JsonElement Data(OutboxMessageView m) => JsonDocument.Parse(m.Payload).RootElement.GetProperty("data");

    private sealed record OutboxMessageView(string EventType, string Payload);

    private List<OutboxMessageView> Events() => store.Outbox.Select(m => new OutboxMessageView(m.EventType, m.Payload)).ToList();

    [Fact]
    public async Task TecnicoAsignado_Cotiza_SolicitudCotizada_YDosEventos()
    {
        var request = Seed();

        var result = Assert.IsType<IssueQuoteResult.Issued>(await Issue(request.Id));

        Assert.Equal(1, result.Quote.QuoteNumber);
        Assert.Equal(ServiceRequestStatus.Cotizado, request.Status);
        Assert.Single(store.Quotes);
        Assert.Equal([EventTypes.Quoted, EventTypes.ServiceRequestStatusChanged], Events().Select(e => e.EventType));

        var quoted = Data(Events()[0]);
        Assert.Equal("185000.00", quoted.GetProperty("totalAmount").GetString());
        Assert.Equal(client, quoted.GetProperty("clientId").GetGuid());
        Assert.Equal(technician, quoted.GetProperty("technicianId").GetGuid());
        Assert.Equal(1, quoted.GetProperty("quoteNumber").GetInt32());
        Assert.Equal(Now + Ttl, quoted.GetProperty("expiresAt").GetDateTimeOffset());
        var changed = Data(Events()[1]);
        Assert.Equal("asignado", changed.GetProperty("previousStatus").GetString());
        Assert.Equal("cotizado", changed.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OtroTecnico_U_OtroTenant_NotFound_SinCambios()
    {
        var request = Seed();
        var ajena = Seed(tenantId: Guid.NewGuid());

        Assert.IsType<IssueQuoteResult.NotFound>(await Issue(request.Id, user: Guid.NewGuid()));
        Assert.IsType<IssueQuoteResult.NotFound>(await Issue(ajena.Id));
        Assert.Equal(ServiceRequestStatus.Asignado, request.Status);
        Assert.Empty(store.Quotes);
        Assert.Empty(store.Outbox);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.BuscandoTecnico)]
    [InlineData(ServiceRequestStatus.Cotizado)]
    [InlineData(ServiceRequestStatus.CotizacionAceptada)]
    [InlineData(ServiceRequestStatus.Cancelado)]
    public async Task FueraDeAsignado_TransicionInvalida(string status)
    {
        var request = Seed(status);

        Assert.IsType<IssueQuoteResult.InvalidTransition>(await Issue(request.Id));
        Assert.Empty(store.Quotes);
    }

    [Fact]
    public async Task MontosInvalidos_NoAbrenTransaccion()
    {
        var request = Seed();

        await Assert.ThrowsAsync<DomainValidationException>(() => Issue(request.Id, labor: "120000"));
        Assert.Equal(0, store.Transactions);
    }

    [Fact]
    public async Task Acepta_CotizacionAceptada_YDosEventos()
    {
        var request = Seed();
        var quote = await IssueOk(request.Id);
        store.Outbox.Clear();

        var result = Assert.IsType<RespondQuoteResult.Responded>(await Respond(request.Id, quote.Id, accept: true));

        Assert.Equal(QuoteStatus.Aceptada, result.Quote.Status);
        Assert.Equal(ServiceRequestStatus.CotizacionAceptada, result.ServiceRequestStatus);
        Assert.Equal(2, result.RemainingQuotes);
        Assert.Equal([EventTypes.QuoteAccepted, EventTypes.ServiceRequestStatusChanged], Events().Select(e => e.EventType));
        Assert.Equal("185000.00", Data(Events()[0]).GetProperty("totalAmount").GetString());
    }

    [Fact]
    public async Task Rechaza_VuelveAAsignado_YPuedeCotizarOtra()
    {
        var request = Seed();
        var quote = await IssueOk(request.Id);
        store.Outbox.Clear();

        var result = Assert.IsType<RespondQuoteResult.Responded>(
            await Respond(request.Id, quote.Id, accept: false, reason: "El valor es muy alto."));

        Assert.Equal(ServiceRequestStatus.Asignado, result.ServiceRequestStatus);
        Assert.Equal(2, result.RemainingQuotes);
        Assert.Equal("El valor es muy alto.", quote.RejectionReason);
        Assert.Equal([EventTypes.QuoteRejected, EventTypes.ServiceRequestStatusChanged], Events().Select(e => e.EventType));
        Assert.Equal(2, Data(Events()[0]).GetProperty("remainingQuotes").GetInt32());
        Assert.False(Data(Events()[0]).TryGetProperty("reason", out _));

        var second = Assert.IsType<IssueQuoteResult.Issued>(await Issue(request.Id, at: Now.AddMinutes(20)));
        Assert.Equal(2, second.Quote.QuoteNumber);
    }

    [Fact]
    public async Task TerceraRechazada_CancelaPorElSistema_YLiberaAlTecnico()
    {
        var request = Seed();
        for (var i = 0; i < 2; i++)
        {
            var q = await IssueOk(request.Id, Now.AddMinutes(i * 10));
            await Respond(request.Id, q.Id, accept: false, at: Now.AddMinutes(i * 10 + 5));
        }

        var third = await IssueOk(request.Id, Now.AddMinutes(30));
        store.Outbox.Clear();

        var result = Assert.IsType<RespondQuoteResult.Responded>(await Respond(request.Id, third.Id, accept: false, at: Now.AddMinutes(35)));

        Assert.Equal(ServiceRequestStatus.Cancelado, result.ServiceRequestStatus);
        Assert.Equal(0, result.RemainingQuotes);
        Assert.Equal(CancellationActor.Sistema, request.CancelledByType);
        Assert.Null(request.CancelledBy);
        Assert.Equal(Quote.RejectedCancellationReason, request.CancellationReason);
        Assert.Equal(
            [EventTypes.QuoteRejected, EventTypes.ServiceRequestCancelled, EventTypes.ServiceRequestStatusChanged],
            Events().Select(e => e.EventType));
        Assert.Equal("sistema", Data(Events()[1]).GetProperty("cancelledByType").GetString());
        Assert.Equal(technician, Data(Events()[1]).GetProperty("technicianId").GetGuid());
    }

    [Fact]
    public async Task CuartaCotizacion_LimiteAlcanzado()
    {
        var request = Seed();
        for (var i = 0; i < 3; i++)
        {
            store.Quotes.Add(Quote.Restore(
                Guid.NewGuid(), tenant, request.Id, technician, 1m, 0m, null, QuoteStatus.Rechazada, Now.AddHours(-3 + i), Now.AddHours(21 + i)));
        }

        Assert.IsType<IssueQuoteResult.LimitReached>(await Issue(request.Id));
    }

    [Fact]
    public async Task RespuestaVencida_U_OtroCliente_NoCambianNada()
    {
        var request = Seed();
        var quote = await IssueOk(request.Id);

        Assert.IsType<RespondQuoteResult.InvalidTransition>(await Respond(request.Id, quote.Id, accept: true, at: quote.ExpiresAt));
        Assert.IsType<RespondQuoteResult.NotFound>(await Respond(request.Id, quote.Id, accept: true, user: Guid.NewGuid()));
        Assert.IsType<RespondQuoteResult.NotFound>(await Respond(request.Id, Guid.NewGuid(), accept: true));
        Assert.Equal(QuoteStatus.Pendiente, quote.Status);
        Assert.Equal(ServiceRequestStatus.Cotizado, request.Status);
    }

    [Fact]
    public async Task ListaParaElClienteYElTecnico_PeroNoParaOtro()
    {
        var request = Seed();
        await IssueOk(request.Id);
        var handler = new ListQuotesHandler(store, store, store);

        var forClient = await handler.HandleAsync(new ListQuotesQuery(tenant, client, false, request.Id), CancellationToken.None);
        var forTechnician = await handler.HandleAsync(new ListQuotesQuery(tenant, technician, true, request.Id), CancellationToken.None);
        var forOther = await handler.HandleAsync(new ListQuotesQuery(tenant, Guid.NewGuid(), true, request.Id), CancellationToken.None);
        var otherTenant = await handler.HandleAsync(new ListQuotesQuery(Guid.NewGuid(), client, false, request.Id), CancellationToken.None);

        Assert.Equal(1, Assert.Single(forClient!).QuoteNumber);
        Assert.Single(forTechnician!);
        Assert.Null(forOther);
        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task Vencimiento_CancelaPorElSistema_EsIdempotente_YCruzaTenants()
    {
        var request = Seed();
        var quote = await IssueOk(request.Id);
        var otherTenant = Guid.NewGuid();
        var other = Seed(ServiceRequestStatus.Cotizado, otherTenant);
        store.Quotes.Add(Quote.Restore(Guid.NewGuid(), otherTenant, other.Id, technician, 1m, 0m, null, QuoteStatus.Pendiente, Now, Now.AddHours(1)));
        store.Outbox.Clear();

        var clock = new FixedClock(quote.ExpiresAt.AddMinutes(1));
        var batch = new ExpireQuotesBatch(store, new ExpireQuoteHandler(store, store, store, store, clock), clock);

        Assert.Equal(2, await batch.RunAsync(100, CancellationToken.None));
        Assert.Equal(QuoteStatus.Expirada, quote.Status);
        Assert.Equal(ServiceRequestStatus.Cancelado, request.Status);
        Assert.Equal(CancellationActor.Sistema, request.CancelledByType);
        Assert.Equal(Quote.ExpiredCancellationReason, request.CancellationReason);
        Assert.Equal(ServiceRequestStatus.Cancelado, other.Status);
        Assert.Equal(4, store.Outbox.Count);

        Assert.Equal(0, await batch.RunAsync(100, CancellationToken.None));
        Assert.Equal(4, store.Outbox.Count);
    }

    [Fact]
    public async Task Vencimiento_NoTocaUnaCotizacionYaRespondida()
    {
        var request = Seed();
        var quote = await IssueOk(request.Id);
        await Respond(request.Id, quote.Id, accept: true);
        var clock = new FixedClock(quote.ExpiresAt.AddMinutes(1));

        var expired = await new ExpireQuoteHandler(store, store, store, store, clock)
            .HandleAsync(new(quote.Id, tenant, request.Id), CancellationToken.None);

        Assert.False(expired);
        Assert.Equal(ServiceRequestStatus.CotizacionAceptada, request.Status);
    }

    [Fact]
    public async Task CancelarConCotizacionPendiente_LaAnula()
    {
        var request = Seed();
        var quote = await IssueOk(request.Id);

        var result = await new CancelServiceRequestHandler(store, store, store, store, store, new FixedClock(Now.AddMinutes(5)))
            .HandleAsync(new CancelServiceRequestCommand(tenant, client, false, request.Id, "Ya no lo necesito.", Guid.NewGuid()),
                CancellationToken.None);

        Assert.IsType<CancelServiceRequestResult.Cancelled>(result);
        Assert.Equal(QuoteStatus.Anulada, quote.Status);
    }
}