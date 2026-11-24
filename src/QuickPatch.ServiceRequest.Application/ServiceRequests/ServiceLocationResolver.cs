using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>
/// Ubicación de una solicitud, común a crearla y a la vista previa (DD 8.3.3): geocodifica la dirección y aplica la
/// cobertura de RN-SR9, que pide que el punto esté dentro del área y caiga en una de las 20 localidades.
/// </summary>
public sealed class ServiceLocationResolver(IGeocoder geocoder, ILocalityCatalog localities, CoverageArea coverageArea)
{
    /// <summary>El punto y la dirección que encontró el geocodificador, o <c>null</c> si no la encontró o no respondió.</summary>
    public Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken cancellationToken) =>
        geocoder.GeocodeAsync(address, cancellationToken);

    /// <summary>La localidad del punto, o <c>null</c> si queda fuera del área de cobertura o de las 20 localidades.</summary>
    public async Task<Locality?> FindLocalityAsync(GeoPoint point, CancellationToken cancellationToken) =>
        coverageArea.Contains(point) ? await localities.FindContainingAsync(point, cancellationToken) : null;
}