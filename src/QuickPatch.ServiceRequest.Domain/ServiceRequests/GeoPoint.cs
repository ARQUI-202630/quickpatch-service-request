namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>Punto geográfico en WGS 84 (SRID 4326).</summary>
public sealed record GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid =>
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}