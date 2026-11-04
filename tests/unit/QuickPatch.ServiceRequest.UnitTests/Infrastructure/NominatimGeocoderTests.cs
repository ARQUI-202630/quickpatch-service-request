using System.Net;
using System.Text;
using System.Web;

using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Infrastructure.Geocoding;

namespace QuickPatch.ServiceRequest.UnitTests.Infrastructure;

/// <summary>Cliente de Nominatim (ADR-024, DD 8.3.3) con un servidor falso.</summary>
public class NominatimGeocoderTests
{
    private const string Chapinero = """[{"lat":"4.6486","lon":"-74.0630","display_name":"Calle 63, Chapinero, Bogotá, Colombia"}]""";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return respond(request, cancellationToken);
        }
    }

    /// <summary>Guarda todo lo que se registra, para comprobar que la dirección nunca llega a los logs (K12).</summary>
    private sealed class CapturingLogger : ILogger<NominatimGeocoder>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception) + exception?.Message);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (NominatimGeocoder Geocoder, FakeHandler Handler, HttpClient Http, CapturingLogger Logger) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, bool withBaseUrl = true, TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        if (withBaseUrl)
        {
            http.BaseAddress = new Uri("http://10.43.98.29:8088/");
        }

        var logger = new CapturingLogger();
        var options = new GeocodingOptions { Timeout = timeout ?? TimeSpan.FromSeconds(3) };
        return (new NominatimGeocoder(http, options, CoverageArea.Bogota, logger), handler, http, logger);
    }

    [Fact]
    public async Task Encontrada_PideUnResultadoAcotadoABogotaYLeeElPunto()
    {
        var (geocoder, handler, http, _) = Create((_, _) => Task.FromResult(Json(Chapinero)));
        using (http)
        {
            var result = await geocoder.GeocodeAsync("Calle 63 # 9-45, Chapinero", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(new GeoPoint(4.6486, -74.063), result.Location);
            Assert.Equal("Calle 63, Chapinero, Bogotá, Colombia", result.MatchedAddress);
            var uri = Assert.Single(handler.Requests);
            Assert.Equal("/search", uri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal("Calle 63 # 9-45, Chapinero", query["q"]);
            Assert.Equal("jsonv2", query["format"]);
            Assert.Equal("1", query["limit"]);
            Assert.Equal("1", query["bounded"]);
            Assert.Equal("-74.25,4.85,-73.98,4.45", query["viewbox"]);
        }
    }

    [Fact]
    public async Task SinResultadoConLaDireccionCompleta_ReintentaConLaViaSola()
    {
        var (geocoder, handler, http, _) = Create((request, _) => Task.FromResult(Json(
            HttpUtility.ParseQueryString(request.RequestUri!.Query)["q"] == "Avenida Caracas" ? Chapinero : "[]")));
        using (http)
        {
            var result = await geocoder.GeocodeAsync("Avenida Caracas # 26-50, local 3", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(2, handler.Requests.Count);
        }
    }

    [Fact]
    public async Task SinResultado_DevuelveNullSinRegistrarLaDireccion()
    {
        var (geocoder, handler, http, logger) = Create((_, _) => Task.FromResult(Json("[]")));
        using (http)
        {
            Assert.Null(await geocoder.GeocodeAsync("Carrera 7 # 72-41", CancellationToken.None));
            Assert.Equal(2, handler.Requests.Count);
            Assert.NotEmpty(logger.Messages);
            Assert.DoesNotContain(logger.Messages, m => m.Contains("Carrera", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SinNumeroDeCasa_NoReintenta()
    {
        var (geocoder, handler, http, _) = Create((_, _) => Task.FromResult(Json("[]")));
        using (http)
        {
            Assert.Null(await geocoder.GeocodeAsync("Parque de la 93", CancellationToken.None));
            Assert.Single(handler.Requests);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RespuestaDeError_DevuelveNull(HttpStatusCode status)
    {
        var (geocoder, _, http, logger) = Create((_, _) => Task.FromResult(Json("{}", status)));
        using (http)
        {
            Assert.Null(await geocoder.GeocodeAsync("Calle 63 # 9-45", CancellationToken.None));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("Calle 63", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("""[{"lat":"x","lon":"-74.06","display_name":"?"}]""")]
    [InlineData("""[{"lat":"95","lon":"-74.06","display_name":"?"}]""")]
    public async Task RespuestaInvalida_DevuelveNull(string body)
    {
        var (geocoder, _, http, _) = Create((_, _) => Task.FromResult(Json(body)));
        using (http)
        {
            Assert.Null(await geocoder.GeocodeAsync("Calle 63", CancellationToken.None));
        }
    }

    [Fact]
    public async Task NoRespondeATiempo_DevuelveNull()
    {
        var (geocoder, _, http, _) = Create(
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return Json(Chapinero);
            },
            timeout: TimeSpan.FromMilliseconds(100));
        using (http)
        {
            Assert.Null(await geocoder.GeocodeAsync("Calle 63 # 9-45", CancellationToken.None));
        }
    }

    [Fact]
    public async Task CancelacionDelLlamador_SePropaga()
    {
        var (geocoder, _, http, _) = Create(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Json(Chapinero);
        });
        using (http)
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => geocoder.GeocodeAsync("Calle 63", cancel.Token));
        }
    }

    [Fact]
    public async Task SinUrlConfigurada_NoLlamaYDevuelveNull()
    {
        var (geocoder, handler, http, _) = Create((_, _) => Task.FromResult(Json(Chapinero)), withBaseUrl: false);
        using (http)
        {
            Assert.Null(await geocoder.GeocodeAsync("Calle 63 # 9-45", CancellationToken.None));
            Assert.Empty(handler.Requests);
        }
    }

    [Theory]
    [InlineData("Avenida Caracas # 26-50", "Avenida Caracas")]
    [InlineData("Calle 63 # 9-45, apto 502", "Calle 63")]
    [InlineData("Calle 63, Chapinero", "Calle 63")]
    [InlineData("Parque de la 93", null)]
    [InlineData("# 9-45", null)]
    [InlineData("Cl 6 # 9-45", null)]
    public void ViaSola(string address, string? expected)
    {
        Assert.Equal(expected, NominatimGeocoder.StreetOnly(address));
    }
}