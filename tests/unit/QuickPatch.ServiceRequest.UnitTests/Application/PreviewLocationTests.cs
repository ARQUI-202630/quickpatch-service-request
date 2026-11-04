using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Application;

/// <summary>Vista previa de la ubicación (contrato 1.3.0, DD 8.3.3).</summary>
public class PreviewLocationTests
{
    private readonly FakeGeocoder geocoder = new();
    private readonly FakeLocalityCatalog localities = new();

    private PreviewLocationHandler Handler() => new(geocoder, localities, CoverageArea.Bogota);

    [Fact]
    public async Task DireccionEncontrada_DevuelvePuntoLocalidadYDireccionReconocida()
    {
        var result = await Handler().HandleAsync("  Calle 63 # 9-45, Chapinero  ", CancellationToken.None);

        var found = Assert.IsType<PreviewLocationResult.Found>(result);
        Assert.Equal(new GeoPoint(4.6486, -74.063), found.Location);
        Assert.Equal(new Locality("02", "Chapinero"), found.Locality);
        Assert.Equal("Calle 63, Chapinero, Bogotá", found.MatchedAddress);
        Assert.Equal("Calle 63 # 9-45, Chapinero", Assert.Single(geocoder.Calls));
    }

    [Fact]
    public async Task NoEncontrada_DireccionNoEncontrada()
    {
        geocoder.Result = null;

        Assert.IsType<PreviewLocationResult.AddressNotFound>(await Handler().HandleAsync("Calle 63 # 9-45", CancellationToken.None));
    }

    [Fact]
    public async Task PuntoFueraDelRectangulo_FueraDeCobertura()
    {
        geocoder.Result = new GeocodedAddress(new GeoPoint(6.2442, -75.5812), "Medellín");

        Assert.IsType<PreviewLocationResult.OutOfCoverage>(await Handler().HandleAsync("Calle 10 # 43-12", CancellationToken.None));
    }

    [Fact]
    public async Task PuntoFueraDeLasLocalidades_FueraDeCobertura()
    {
        localities.Result = null;

        Assert.IsType<PreviewLocationResult.OutOfCoverage>(await Handler().HandleAsync("Calle 63 # 9-45", CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Cl 6")]
    public async Task DireccionInvalida_ValidaSinGeocodificar(string? address)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Handler().HandleAsync(address, CancellationToken.None));

        Assert.Equal(["addressText"], ex.Errors.Keys);
        Assert.Empty(geocoder.Calls);
    }
}