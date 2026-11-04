using QuickPatch.ServiceRequest.Domain.Tenants;

namespace QuickPatch.ServiceRequest.UnitTests.Domain;

public class TenantStatusReplicaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("activo", true)]
    [InlineData("inactivo", true)]
    [InlineData("suspendido", false)]
    [InlineData("ACTIVO", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidStatus_SoloAceptaLosValoresDelContrato(string? status, bool expected) =>
        Assert.Equal(expected, TenantStatusReplica.IsValidStatus(status));

    [Fact]
    public void IsInactive_SoloConEstadoInactivo()
    {
        Assert.True(new TenantStatusReplica(Guid.NewGuid(), TenantStatusReplica.Inactive, T0).IsInactive);
        Assert.False(new TenantStatusReplica(Guid.NewGuid(), TenantStatusReplica.Active, T0).IsInactive);
    }

    [Fact]
    public void ApplyChange_ConUnaVersionMasReciente_CambiaElEstado()
    {
        var replica = new TenantStatusReplica(Guid.NewGuid(), TenantStatusReplica.Active, T0);

        var changed = replica.ApplyChange(TenantStatusReplica.Inactive, T0.AddSeconds(1));

        Assert.True(changed);
        Assert.True(replica.IsInactive);
        Assert.Equal(T0.AddSeconds(1), replica.UpdatedAt);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(0)]
    public void ApplyChange_ConUnaVersionAnticuadaOIgual_SeDescarta(int seconds)
    {
        var replica = new TenantStatusReplica(Guid.NewGuid(), TenantStatusReplica.Inactive, T0);

        var changed = replica.ApplyChange(TenantStatusReplica.Active, T0.AddSeconds(seconds));

        Assert.False(changed);
        Assert.True(replica.IsInactive);
        Assert.Equal(T0, replica.UpdatedAt);
    }
}