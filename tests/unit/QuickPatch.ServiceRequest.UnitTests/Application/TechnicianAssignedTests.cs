using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.Matching;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Infrastructure.Messaging;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary><c>matching.technician-assigned</c> en el caso de uso y en el consumidor de Matching (SCRUM-91).</summary>
public class TechnicianAssignedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly DomainServiceRequest request;

    public TechnicianAssignedTests()
    {
        request = DomainServiceRequest.Create(tenant, Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", Now);
        store.ServiceRequests.Add(request);
    }

    private MatchingStatusHandler Handler() =>
        new(store, store, store, store, new FixedClock(Now), NullLogger<MatchingStatusHandler>.Instance);

    private EventEnvelope<TechnicianAssignedData> Asignada(Guid technician, Guid? tenantId = null, Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), EventTypes.TechnicianAssigned, 1, Now.AddMinutes(1), Guid.NewGuid(), tenantId ?? tenant,
            "matching-service", new TechnicianAssignedData(request.Id, request.ClientId, Guid.NewGuid(), technician, Now.AddMinutes(1)));

    [Fact]
    public async Task Asignada_PasaAAsignadoYRegistraElEvento()
    {
        var technician = Guid.NewGuid();
        var evento = Asignada(technician);

        var outcome = await Handler().HandleTechnicianAssignedAsync(evento, CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.Applied, outcome);
        Assert.Equal(ServiceRequestStatus.Asignado, request.Status);
        Assert.Equal(technician, request.TechnicianId);
        Assert.Equal(Now.AddMinutes(1), request.AssignedAt);
        Assert.Equal((evento.EventId, tenant, EventTypes.TechnicianAssigned), Assert.Single(store.ProcessedEvents));

        var data = JsonDocument.Parse(Assert.Single(store.Outbox).Payload).RootElement.GetProperty("data");
        Assert.Equal(technician, data.GetProperty("technicianId").GetGuid());
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, data.GetProperty("previousStatus").GetString());
        Assert.Equal(ServiceRequestStatus.Asignado, data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AsignadaDespuesDeEnEspera_NoCambiaElEstadoNiRegistraElAviso()
    {
        // Caso de SCRUM-466: Matching asigna, pero a ServiceRequest ya le llegó no-technician-available más reciente.
        await Handler().HandleNoTechnicianAvailableAsync(
            new EventEnvelope<NoTechnicianAvailableData>(Guid.NewGuid(), EventTypes.NoTechnicianAvailable, 1, Now.AddMinutes(2), Guid.NewGuid(),
                tenant, "matching-service", new NoTechnicianAvailableData(request.Id, request.ClientId, 3)),
            CancellationToken.None);

        var outcome = await Handler().HandleTechnicianAssignedAsync(Asignada(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.Stale, outcome);
        Assert.Equal(ServiceRequestStatus.EnEspera, request.Status);
        Assert.Null(request.TechnicianId);
        var estado = Assert.Single(store.Outbox);
        Assert.Equal(
            ServiceRequestStatus.EnEspera,
            JsonDocument.Parse(estado.Payload).RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task MismoEventoDosVeces_SeAplicaUnaSola()
    {
        var evento = Asignada(Guid.NewGuid());

        await Handler().HandleTechnicianAssignedAsync(evento, CancellationToken.None);
        var segunda = await Handler().HandleTechnicianAssignedAsync(evento, CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.Duplicate, segunda);
    }

    [Fact]
    public async Task EventoDeOtroTenant_NoTocaLaSolicitud()
    {
        var outcome = await Handler().HandleTechnicianAssignedAsync(Asignada(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(MatchingEventHandlingOutcome.NotFound, outcome);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
        Assert.Null(request.TechnicianId);
    }

    [Fact]
    public async Task EventoNulo_LanzaExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleTechnicianAssignedAsync(null!, CancellationToken.None));
    }

    private MatchingEventsConsumer Consumer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddSingleton<ITenantUnitOfWork>(store);
        services.AddSingleton<IServiceRequestRepository>(store);
        services.AddSingleton<IProcessedEventRepository>(store);
        services.AddSingleton<IOutbox>(store);
        services.AddScoped<MatchingStatusHandler>();
        var kafka = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = "localhost:1", GroupId = "pruebas" }).Build();
        return new MatchingEventsConsumer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kafka, NullLogger<MatchingEventsConsumer>.Instance);
    }

    private string Mensaje(object data) => JsonSerializer.Serialize(new
    {
        eventId = Guid.NewGuid(),
        eventType = "matching.technician-assigned",
        eventVersion = 1,
        occurredAt = Now.AddMinutes(1),
        correlationId = Guid.NewGuid(),
        tenantId = tenant,
        producer = "matching-service",
        data,
    });

    [Fact]
    public async Task Consumidor_LeeElContratoYAsigna()
    {
        var technician = Guid.NewGuid();
        using var consumer = Consumer();

        var ok = await consumer.TryProcessAsync(
            Mensaje(new { serviceRequestId = request.Id, clientId = request.ClientId, attemptId = Guid.NewGuid(), technicianId = technician, assignedAt = Now.AddMinutes(1) }),
            CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(EventTypes.TechnicianAssigned, MatchingEventsConsumer.Topics);
        Assert.Equal(ServiceRequestStatus.Asignado, request.Status);
        Assert.Equal(technician, request.TechnicianId);
        Assert.Equal(EventTypes.ServiceRequestStatusChanged, Assert.Single(store.Outbox).EventType);
    }

    [Fact]
    public async Task Consumidor_AsignacionSinTecnico_SeDescartaSinBloquear()
    {
        using var consumer = Consumer();

        var ok = await consumer.TryProcessAsync(
            Mensaje(new { serviceRequestId = request.Id, clientId = request.ClientId, attemptId = Guid.NewGuid() }), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
        Assert.Empty(store.ProcessedEvents);
        Assert.Empty(store.Outbox);
    }
}