using System.Text.Json;

using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Cancelación antes de iniciar el servicio (RF-36, RN-SR7, RN-SR8; contrato 1.5.0, DD 8.3.9).</summary>
public class CancelServiceRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 16, 20, 0, TimeSpan.Zero);
    private readonly InMemoryStore store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();

    private DomainServiceRequest Seed(string status, Guid? technician = null, Guid? tenantId = null, Guid? clientId = null)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenantId ?? tenant, clientId ?? client, Guid.NewGuid(), technician, "Fuga de agua en la cocina",
            new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", status, Now.AddHours(-1), technician is null ? null : Now.AddMinutes(-30));
        store.ServiceRequests.Add(request);
        return request;
    }

    private Task<CancelServiceRequestResult> Cancel(Guid id, Guid? user = null, bool admin = false, string? reason = "Ya no necesito el servicio.") =>
        new CancelServiceRequestHandler(store, store, store, store, store, new FixedClock(Now))
            .HandleAsync(new CancelServiceRequestCommand(tenant, user ?? client, admin, id, reason, Guid.NewGuid()), CancellationToken.None);

    [Theory]
    [InlineData(ServiceRequestStatus.BuscandoTecnico)]
    [InlineData(ServiceRequestStatus.EnEspera)]
    [InlineData(ServiceRequestStatus.Asignado)]
    [InlineData(ServiceRequestStatus.Cotizado)]
    [InlineData(ServiceRequestStatus.CotizacionAceptada)]
    public async Task ClienteDuenio_CancelaAntesDeIniciar_YDejaLosDosEventos(string status)
    {
        var technician = Guid.NewGuid();
        var request = Seed(status, status == ServiceRequestStatus.BuscandoTecnico ? null : technician);

        var result = await Cancel(request.Id, reason: "  Ya no necesito el servicio.  ");

        var cancelled = Assert.IsType<CancelServiceRequestResult.Cancelled>(result).ServiceRequest;
        Assert.Equal(ServiceRequestStatus.Cancelado, cancelled.Status);
        Assert.Equal(Now, cancelled.CancelledAt);
        Assert.Equal("Ya no necesito el servicio.", cancelled.CancellationReason);
        Assert.Equal(client, cancelled.CancelledBy);
        Assert.Equal(CancellationActor.Cliente, cancelled.CancelledByType);
        Assert.Equal(
            [EventTypes.ServiceRequestCancelled, EventTypes.ServiceRequestStatusChanged],
            store.Outbox.Select(m => m.EventType));
        Assert.Empty(store.Audit);

        var changed = JsonDocument.Parse(store.Outbox[1].Payload).RootElement.GetProperty("data");
        Assert.Equal(status, changed.GetProperty("previousStatus").GetString());
        Assert.Equal("cancelado", changed.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EventoCancelled_LlevaLosCamposDelContrato()
    {
        var technician = Guid.NewGuid();
        var request = Seed(ServiceRequestStatus.Asignado, technician);

        await Cancel(request.Id);

        var root = JsonDocument.Parse(store.Outbox[0].Payload).RootElement;
        Assert.Equal("service-request.cancelled", root.GetProperty("eventType").GetString());
        Assert.Equal(tenant, root.GetProperty("tenantId").GetGuid());
        var data = root.GetProperty("data");
        Assert.Equal(
            ["cancelledAt", "cancelledByType", "clientId", "reason", "serviceRequestId", "technicianId"],
            data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(technician, data.GetProperty("technicianId").GetGuid());
        Assert.Equal("cliente", data.GetProperty("cancelledByType").GetString());
        Assert.Equal(request.Id, store.Outbox[0].AggregateId);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.EnProgreso)]
    [InlineData(ServiceRequestStatus.Completado)]
    [InlineData(ServiceRequestStatus.Pagado)]
    [InlineData(ServiceRequestStatus.Cancelado)]
    public async Task DesdeEnProgresoOTerminada_TransicionInvalida_SinEventos(string status)
    {
        var request = Seed(status, Guid.NewGuid());

        var result = await Cancel(request.Id);

        Assert.Equal(status, Assert.IsType<CancelServiceRequestResult.InvalidTransition>(result).Status);
        Assert.Equal(status, request.Status);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task OtroCliente_NoEncontrada_SinCambios()
    {
        var request = Seed(ServiceRequestStatus.BuscandoTecnico, clientId: Guid.NewGuid());

        Assert.IsType<CancelServiceRequestResult.NotFound>(await Cancel(request.Id));
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task OtroTenant_NoEncontrada_AunqueSeaAdmin()
    {
        var request = Seed(ServiceRequestStatus.BuscandoTecnico, tenantId: Guid.NewGuid(), clientId: client);

        Assert.IsType<CancelServiceRequestResult.NotFound>(await Cancel(request.Id));
        Assert.IsType<CancelServiceRequestResult.NotFound>(await Cancel(request.Id, Guid.NewGuid(), admin: true));
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
    }

    [Fact]
    public async Task AdminDelTenant_CancelaCualquiera_YQuedaEnAuditoria()
    {
        var admin = Guid.NewGuid();
        var request = Seed(ServiceRequestStatus.EnEspera, clientId: Guid.NewGuid());

        var result = await Cancel(request.Id, admin, admin: true);

        var cancelled = Assert.IsType<CancelServiceRequestResult.Cancelled>(result).ServiceRequest;
        Assert.Equal(CancellationActor.AdminTenant, cancelled.CancelledByType);
        Assert.Equal(admin, cancelled.CancelledBy);
        var audit = Assert.Single(store.Audit);
        Assert.Equal(CancelServiceRequestHandler.AuditAction, audit.Action);
        Assert.Equal((tenant, admin, request.Id), (audit.TenantId, audit.ActorUserId, audit.TargetId));
        Assert.DoesNotContain("necesito", audit.Metadata, StringComparison.Ordinal);
        Assert.Equal("en_espera", JsonDocument.Parse(audit.Metadata).RootElement.GetProperty("previousStatus").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("nop")]
    public async Task MotivoInvalido_ErrorDeReason_SinTransaccion(string? reason)
    {
        var request = Seed(ServiceRequestStatus.BuscandoTecnico);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Cancel(request.Id, reason: reason));

        Assert.Equal(["reason"], ex.Errors.Keys);
        Assert.Equal(0, store.Transactions);
    }

    [Fact]
    public async Task MotivoDe501Caracteres_Invalido()
    {
        var request = Seed(ServiceRequestStatus.BuscandoTecnico);

        await Assert.ThrowsAsync<DomainValidationException>(() => Cancel(request.Id, reason: new string('a', 501)));
    }

    [Fact]
    public void Dominio_TipoDeCancelacionDesconocido_Lanza()
    {
        var request = Seed(ServiceRequestStatus.BuscandoTecnico);

        Assert.Throws<ArgumentException>(() => request.TryCancel(client, "tecnico", "Ya no lo necesito", Now));
    }
}