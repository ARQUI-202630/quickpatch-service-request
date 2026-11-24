using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

public abstract record PreviewLocationResult
{
    public sealed record Found(GeoPoint Location, Locality Locality, string MatchedAddress) : PreviewLocationResult;

    /// <summary>El geocodificador no encontró la dirección o no respondió a tiempo.</summary>
    public sealed record AddressNotFound : PreviewLocationResult;

    /// <summary>RN-SR9: el punto quedó fuera del área de cobertura o de las 20 localidades.</summary>
    public sealed record OutOfCoverage : PreviewLocationResult;
}

/// <summary>
/// Vista previa de la ubicación (<c>POST /v1/service-requests/location-preview</c>, contrato 1.3.0, DD 8.3.3): geocodifica
/// la dirección igual que al crear y devuelve el punto y su localidad, sin crear nada. Nominatim no usa el número de casa,
/// así que el cliente confirma o mueve el pin antes de crear la solicitud con <c>location</c>.
/// </summary>
public sealed class PreviewLocationHandler(ServiceLocationResolver locations)
{
    public async Task<PreviewLocationResult> HandleAsync(string? addressText, CancellationToken cancellationToken)
    {
        var address = ServiceRequestRules.ValidateAddress(addressText);

        var geocoded = await locations.GeocodeAsync(address, cancellationToken);
        if (geocoded is null)
        {
            return new PreviewLocationResult.AddressNotFound();
        }

        var locality = await locations.FindLocalityAsync(geocoded.Location, cancellationToken);
        return locality is null
            ? new PreviewLocationResult.OutOfCoverage()
            : new PreviewLocationResult.Found(geocoded.Location, locality, geocoded.MatchedAddress);
    }
}