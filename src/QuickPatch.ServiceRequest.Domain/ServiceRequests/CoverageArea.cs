namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>
/// Área de cobertura del servicio (RN-SR9, restricción R6 del SAD): un rectángulo de coordenadas.
/// El valor real viene de la configuración; <see cref="Bogota"/> es el valor inicial del DD.
/// </summary>
public sealed record CoverageArea(double MinLatitude, double MaxLatitude, double MinLongitude, double MaxLongitude)
{
    public static CoverageArea Bogota { get; } = new(4.45, 4.85, -74.25, -73.98);

    public bool Contains(GeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.Latitude >= MinLatitude && point.Latitude <= MaxLatitude
            && point.Longitude >= MinLongitude && point.Longitude <= MaxLongitude;
    }
}