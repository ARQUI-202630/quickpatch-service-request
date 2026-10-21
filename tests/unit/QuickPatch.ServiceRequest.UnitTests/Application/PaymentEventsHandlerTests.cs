using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Payments;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Infrastructure.Messaging;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Eventos de Payments (SCRUM-115): <c>payment.approved</c> pasa a pagado (RN-SR4); el rechazo no cambia nada (RN-SR5).</summary>
public class PaymentEventsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly DomainServiceRequest request;

    public PaymentEventsHandlerTests()
    {
        request = DomainServiceRequest.Create(tenant, Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", Now);
        request.ApplyTechnicianAssigned(Guid.NewGuid(), Now, Now);
        Assert.True(request.TryMarkQuoted());
        Assert.True(request.TryAcceptQuote());
        Assert.True(request.TryStart(Now));
        Assert.True(request.TryComplete(Now));
        store.ServiceRequests.Add(request);
    }

    private PaymentEventsHandler Handler() =>
        new(store, store, store, store, new FixedClock(Now), NullLogger<PaymentEventsHandler>.Instance);

    private EventEnvelope<PaymentApprovedData> Approved(Guid? eventId = null, Guid? requestId = null) =>
        new(eventId ?? Guid.NewGuid(), EventTypes.PaymentApproved, 1, Now, Guid.NewGuid(), tenant, "payments-service",
            new PaymentApprovedData(Guid.NewGuid(), requestId ?? request.Id, "185000.00", Guid.NewGuid(), Now));

    private EventEnvelope<PaymentRejectedData> Rejected() =>
        new(Guid.NewGuid(), EventTypes.PaymentRejected, 1, Now, Guid.NewGuid(), tenant, "payments-service",
            new PaymentRejectedData(Guid.NewGuid(), request.Id, "fondos_insuficientes", Now));

    [Fact]
    public async Task Aprobado_PasaAPagado_YPublicaElCambioDeEstado()
    {
        var evento = Approved();

        var outcome = await Handler().HandleApprovedAsync(evento, CancellationToken.None);

        Assert.Equal(PaymentEventOutcome.Paid, outcome);
        Assert.Equal(ServiceRequestStatus.Pagado, request.Status);
        var mensaje = Assert.Single(store.Outbox);
        Assert.Equal(EventTypes.ServiceRequestStatusChanged, mensaje.EventType);
        using var json = JsonDocument.Parse(mensaje.Payload);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(ServiceRequestStatus.Completado, data.GetProperty("previousStatus").GetString());
        Assert.Equal(ServiceRequestStatus.Pagado, data.GetProperty("status").GetString());
        Assert.Equal(evento.CorrelationId, json.RootElement.GetProperty("correlationId").GetGuid());
    }

    [Fact]
    public async Task Aprobado_Repetido_NoPublicaDosVeces_YOtroAprobado_SeIgnora()
    {
        var evento = Approved();
        await Handler().HandleApprovedAsync(evento, CancellationToken.None);

        Assert.Equal(PaymentEventOutcome.Duplicate, await Handler().HandleApprovedAsync(evento, CancellationToken.None));
        Assert.Equal(PaymentEventOutcome.Ignored, await Handler().HandleApprovedAsync(Approved(), CancellationToken.None));
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Rechazado_SigueEnCompletado_SinEvento()
    {
        Assert.Equal(PaymentEventOutcome.Rejected, await Handler().HandleRejectedAsync(Rejected(), CancellationToken.None));

        Assert.Equal(ServiceRequestStatus.Completado, request.Status);
        Assert.Empty(store.Outbox);
        Assert.Single(store.ProcessedEvents);
    }

    [Fact]
    public async Task SolicitudInexistente_NoEncontrada()
    {
        Assert.Equal(PaymentEventOutcome.NotFound, await Handler().HandleApprovedAsync(Approved(requestId: Guid.NewGuid()), CancellationToken.None));
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public void SoloDesdeCompletado()
    {
        var nueva = DomainServiceRequest.Create(tenant, Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", Now);

        Assert.False(nueva.TryMarkPaid());
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, nueva.Status);
    }

    private PaymentEventsConsumer Consumer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddSingleton<ITenantUnitOfWork>(store);
        services.AddSingleton<IServiceRequestRepository>(store);
        services.AddSingleton<IProcessedEventRepository>(store);
        services.AddSingleton<IOutbox>(store);
        services.AddScoped<PaymentEventsHandler>();
        var kafka = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = "localhost:1", GroupId = "pruebas" }).Build();
        return new PaymentEventsConsumer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kafka, NullLogger<PaymentEventsConsumer>.Instance);
    }

    private string Mensaje(string type, object data) => JsonSerializer.Serialize(new
    {
        eventId = Guid.NewGuid(),
        eventType = type,
        eventVersion = 1,
        occurredAt = Now,
        correlationId = Guid.NewGuid(),
        tenantId = tenant,
        producer = "payments-service",
        data,
    });

    [Fact]
    public async Task Consumidor_LeeElContratoCompleto_YDescartaLoInvalido()
    {
        using var consumer = Consumer();
        var completo = new
        {
            paymentId = Guid.NewGuid(),
            serviceRequestId = request.Id,
            clientId = request.ClientId,
            companyId = (Guid?)null,
            technicianId = request.TechnicianId,
            amount = "185000.00",
            invoiceId = Guid.NewGuid(),
            approvedAt = Now,
        };

        Assert.True(await consumer.TryProcessAsync(
            Mensaje("payment.rejected", new { paymentId = Guid.NewGuid(), serviceRequestId = request.Id, reasonCode = "fondos_insuficientes", rejectedAt = Now }),
            CancellationToken.None));
        Assert.Equal(ServiceRequestStatus.Completado, request.Status);

        Assert.True(await consumer.TryProcessAsync(Mensaje("payment.approved", completo), CancellationToken.None));
        Assert.Equal(ServiceRequestStatus.Pagado, request.Status);

        Assert.True(await consumer.TryProcessAsync(Mensaje("payment.approved", new { paymentId = Guid.NewGuid() }), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Mensaje("payment.rejected", new { paymentId = Guid.NewGuid() }), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Mensaje("payment.otro", completo), CancellationToken.None));
        Assert.Equal([EventTypes.PaymentApproved, EventTypes.PaymentRejected], PaymentEventsConsumer.Topics);
        Assert.Single(store.Outbox);
    }
}