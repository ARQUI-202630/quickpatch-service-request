using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Domain;

public class ServiceRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Chapinero = new(4.6486, -74.063);

    [Fact]
    public void Create_DatosValidos_QuedaEnBuscandoTecnicoSinTecnico()
    {
        var tenant = Guid.NewGuid();
        var client = Guid.NewGuid();
        var category = Guid.NewGuid();

        var request = DomainServiceRequest.Create(tenant, client, category, "  Fuga de agua en la cocina  ", Chapinero, " Calle 63 # 9-45 ", Now);

        Assert.NotEqual(Guid.Empty, request.Id);
        Assert.Equal(tenant, request.TenantId);
        Assert.Equal(client, request.ClientId);
        Assert.Equal(category, request.CategoryId);
        Assert.Equal(ServiceRequestStatus.BuscandoTecnico, request.Status);
        Assert.Null(request.TechnicianId);
        Assert.Equal("Fuga de agua en la cocina", request.Description);
        Assert.Equal("Calle 63 # 9-45", request.AddressText);
        Assert.Equal(Now, request.CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("corta")]
    public void Create_DescripcionFueraDeRango_Falla(string? descripcion)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            DomainServiceRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), descripcion, Chapinero, "Calle 63 # 9-45", Now));

        Assert.True(ex.Errors.ContainsKey("description"));
    }

    [Fact]
    public void Create_DescripcionDemasiadoLarga_Falla()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            DomainServiceRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 1001), Chapinero, "Calle 63 # 9-45", Now));

        Assert.True(ex.Errors.ContainsKey("description"));
    }

    [Fact]
    public void Create_VariosErrores_LosReportaTodosPorCampo()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            DomainServiceRequest.Create(Guid.Empty, Guid.Empty, Guid.Empty, "Fuga de agua en la cocina", new GeoPoint(91, 0), "abc", Now));

        Assert.Equal(["addressText", "categoryId", "clientId", "location", "tenantId"], ex.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Create_SinUbicacion_Falla()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            DomainServiceRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina", null, "Calle 63 # 9-45", Now));

        Assert.True(ex.Errors.ContainsKey("location"));
    }

    [Fact]
    public void Restore_ConservaEstadoYTecnico()
    {
        var technician = Guid.NewGuid();

        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), technician, "Fuga de agua", Chapinero, "Calle 63", ServiceRequestStatus.Asignado, Now);

        Assert.Equal(ServiceRequestStatus.Asignado, request.Status);
        Assert.Equal(technician, request.TechnicianId);
    }

    [Fact]
    public void DomainValidationException_ConstructoresEstandar()
    {
        Assert.Empty(new DomainValidationException().Errors);
        Assert.Equal("x", new DomainValidationException("x").Message);
        Assert.Empty(new DomainValidationException("x", new InvalidOperationException()).Errors);
    }
}

public class CoverageAreaTests
{
    [Theory]
    [InlineData(4.6486, -74.063, true)] // Chapinero
    [InlineData(4.7431, -74.0886, true)] // Suba
    [InlineData(6.2442, -75.5812, false)] // Medellín
    [InlineData(4.86, -74.05, false)] // al norte del límite
    public void Bogota_ContieneSoloPuntosDelAreaUrbana(double latitud, double longitud, bool esperado)
    {
        Assert.Equal(esperado, CoverageArea.Bogota.Contains(new GeoPoint(latitud, longitud)));
    }

    [Fact]
    public void Contains_SinPunto_LanzaExcepcion()
    {
        Assert.Throws<ArgumentNullException>(() => CoverageArea.Bogota.Contains(null!));
    }

    [Theory]
    [InlineData(-91, 0, false)]
    [InlineData(0, 181, false)]
    [InlineData(90, -180, true)]
    public void GeoPoint_ValidaRangos(double latitud, double longitud, bool esperado)
    {
        Assert.Equal(esperado, new GeoPoint(latitud, longitud).IsValid);
    }
}

public class CategoryReplicaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApplyChange_MasReciente_Actualiza()
    {
        var category = new CategoryReplica(Guid.NewGuid(), Guid.NewGuid(), "Plomería", true, T0);

        Assert.True(category.ApplyChange("Plomería y gas", false, T0.AddMinutes(1)));
        Assert.Equal("Plomería y gas", category.Name);
        Assert.False(category.Active);
        Assert.Equal(T0.AddMinutes(1), category.UpdatedAt);
    }

    [Fact]
    public void ApplyChange_AtrasadoOIgual_SeDescarta()
    {
        var category = new CategoryReplica(Guid.NewGuid(), Guid.NewGuid(), "Plomería", true, T0);

        Assert.False(category.ApplyChange("Viejo", false, T0));
        Assert.False(category.ApplyChange("Viejo", false, T0.AddMinutes(-5)));
        Assert.Equal("Plomería", category.Name);
        Assert.True(category.Active);
    }
}