using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

namespace QuickPatch.ServiceRequest.Infrastructure.Geocoding;

/// <summary>Sección <c>Geocoding</c>: el Nominatim propio de la VM de datos del ambiente (ADR-024, DD 8.3.3).</summary>
public sealed class GeocodingOptions
{
    public const string Section = "Geocoding";

    /// <summary>
    /// Por ejemplo <c>http://10.43.98.29:8088</c> en QA (Nginx de VM5). Vacío: no se geocodifica y una solicitud sin
    /// <c>location</c> responde <c>422 direccion-no-encontrada</c>.
    /// </summary>
    public string NominatimUrl { get; set; } = string.Empty;

    /// <summary>Espera máxima de toda la geocodificación, reintento incluido. Corta: el cliente está esperando.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Cliente de <c>GET /search</c> de Nominatim, acotado al rectángulo de cobertura (<c>viewbox</c> con <c>bounded=1</c>).
/// Nominatim no entiende el número de casa de la nomenclatura de Bogotá ("# 26-50"): si la dirección completa no da
/// resultado, reintenta una vez con la vía sola, y el cliente confirma el punto en la vista previa. Ante cualquier falla
/// devuelve null y registra solo el motivo: la dirección nunca va a los logs (K12).
/// </summary>
public sealed partial class NominatimGeocoder(
    HttpClient http, GeocodingOptions options, CoverageArea coverageArea, ILogger<NominatimGeocoder> logger) : IGeocoder
{
    public const string SearchPath = "search";

    public async Task<GeocodedAddress?> GeocodeAsync(string addressText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addressText);
        if (http.BaseAddress is null || string.IsNullOrWhiteSpace(addressText))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            var result = await SearchAsync(addressText.Trim(), timeout.Token);
            var street = StreetOnly(addressText);
            if (result is null && street is not null)
            {
                result = await SearchAsync(street, timeout.Token);
            }

            if (result is null)
            {
                LogNotFound(logger);
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // OperationCanceledException sin cancelación del llamador es el tiempo de espera.
            LogFailed(logger, ex.GetType().Name, ex);
            return null;
        }
    }

    /// <summary>
    /// La vía sin el número de casa ni los complementos: "Avenida Caracas # 26-50, local 3" queda en "Avenida Caracas".
    /// Null si no hay nada que quitar.
    /// </summary>
    public static string? StreetOnly(string addressText)
    {
        ArgumentNullException.ThrowIfNull(addressText);
        var cut = addressText.IndexOfAny(['#', ',']);
        if (cut <= 0)
        {
            return null;
        }

        var street = addressText[..cut].Trim();
        return street.Length >= Domain.ServiceRequests.ServiceRequest.AddressMinLength ? street : null;
    }

    private async Task<GeocodedAddress?> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var c = coverageArea;
        var viewbox = string.Create(
            CultureInfo.InvariantCulture, $"{c.MinLongitude},{c.MaxLatitude},{c.MaxLongitude},{c.MinLatitude}");
        var uri = $"{SearchPath}?format=jsonv2&limit=1&countrycodes=co&bounded=1&accept-language=es"
            + $"&viewbox={Uri.EscapeDataString(viewbox)}&q={Uri.EscapeDataString(query)}";

        using var response = await http.GetAsync(new Uri(uri, UriKind.Relative), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        var places = await response.Content.ReadFromJsonAsync<List<Place>>(cancellationToken);
        var place = places?.FirstOrDefault();
        if (place is null
            || !double.TryParse(place.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(place.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
        {
            return null;
        }

        var point = new GeoPoint(latitude, longitude);
        return point.IsValid ? new GeocodedAddress(point, place.DisplayName ?? string.Empty) : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Nominatim no encontró la dirección.")]
    private static partial void LogNotFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo geocodificar ({Reason}).")]
    private static partial void LogFailed(ILogger logger, string reason, Exception? exception);

    private sealed record Place(
        [property: JsonPropertyName("lat")] string? Lat,
        [property: JsonPropertyName("lon")] string? Lon,
        [property: JsonPropertyName("display_name")] string? DisplayName);
}