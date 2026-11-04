using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Domain;

/// <summary><c>matching.technician-assigned</c> (DD 7.4, SCRUM-91) y la visibilidad para el técnico asignado (RN-SR13).</summary>
public class AssignmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private static DomainServiceRequest InStatus(string status) =>
        DomainServiceRequest.Restore(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Fuga de agua", new GeoPoint(4.6486, -74.063), "Calle 63", status, Now);

    [Fact]
    public void Asignacion_DesdeBuscando_GuardaTecnicoYHora()
    {
        var request = InStatus(ServiceRequestStatus.BuscandoTecnico);
        var technician = Guid.NewGuid();

        var outcome = request.ApplyTechnicianAssigned(technician, Now.AddMinutes(2), Now.AddMinutes(2));

        Assert.Equal(MatchingEventOutcome.Applied, outcome);
        Assert.Equal(ServiceRequestStatus.Asignado, request.Status);
        Assert.Equal(technician, request.TechnicianId);
        Assert.Equal(Now.AddMinutes(2), request.AssignedAt);
        Assert.Equal(Now.AddMinutes(2), request.MatchingUpdatedAt);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.EnEspera)]
    [InlineData(ServiceRequestStatus.Cancelado)]
    public void Asignacion_DesdeOtroEstado_NoGuardaTecnico(string status)
    {
        var request = InStatus(status);

        var outcome = request.ApplyTechnicianAssigned(Guid.NewGuid(), Now, Now);

        Assert.Equal(MatchingEventOutcome.Ignored, outcome);
        Assert.Equal(status, request.Status);
        Assert.Null(request.TechnicianId);
        Assert.Null(request.AssignedAt);
    }

    [Fact]
    public void Asignacion_MasViejaQueElUltimoEvento_SeDescarta()
    {
        var request = InStatus(ServiceRequestStatus.BuscandoTecnico);
        request.ApplyNoTechnicianAvailable(Now.AddMinutes(5));
        request.ApplySearchResumed(Now.AddMinutes(6));

        var outcome = request.ApplyTechnicianAssigned(Guid.NewGuid(), Now.AddMinutes(1), Now.AddMinutes(1));

        Assert.Equal(MatchingEventOutcome.Stale, outcome);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
        Assert.Null(request.TechnicianId);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.BuscandoTecnico, false)]
    [InlineData(ServiceRequestStatus.EnEspera, false)]
    [InlineData(ServiceRequestStatus.Asignado, true)]
    [InlineData(ServiceRequestStatus.Cotizado, true)]
    [InlineData(ServiceRequestStatus.CotizacionAceptada, true)]
    [InlineData(ServiceRequestStatus.EnProgreso, true)]
    [InlineData(ServiceRequestStatus.Completado, true)]
    [InlineData(ServiceRequestStatus.Pagado, false)]
    [InlineData(ServiceRequestStatus.Cancelado, false)]
    public void TecnicoAsignado_SoloVeEntreAsignadoYCompletado(string status, bool visible)
    {
        Assert.Equal(visible, InStatus(status).IsVisibleToAssignedTechnician);
    }
}