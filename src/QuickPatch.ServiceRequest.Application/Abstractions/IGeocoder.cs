using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>
/// Geocodificador de direcciones dentro de Bogotá: el Nominatim propio de la VM de datos (ADR-024, DD 8.3.3).
/// </summary>
public interface IGeocoder
{
    /// <summary>
    /// Devuelve el punto y lo que reconoció el geocodificador, o null si no encontró la dirección, no está configurado
    /// o no respondió a tiempo. La dirección es un dato personal: nunca se registra en logs (K12).
    /// </summary>
    Task<GeocodedAddress?> GeocodeAsync(string addressText, CancellationToken cancellationToken);
}

public sealed record GeocodedAddress(GeoPoint Location, string MatchedAddress);