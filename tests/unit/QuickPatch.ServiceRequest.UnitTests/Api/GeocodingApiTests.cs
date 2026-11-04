using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>Crear sin <c>location</c> y la vista previa de la ubicación (contrato 1.3.0, ADR-024).</summary>
public class GeocodingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();
    private static readonly Uri CreateUri = new("/v1/service-requests", UriKind.Relative);
    private static readonly Uri PreviewUri = new("/v1/service-requests/location-preview", UriKind.Relative);
    private static readonly GeocodedAddress Chapinero = new(new GeoPoint(4.6486, -74.063), "Calle 63, Chapinero, Bogotá");

    private HttpClient Client(string role = "cliente")
    {
        lock (factory.Store)
        {
            if (!factory.Store.Categories.Any(c => c.CategoryId == Category))
            {
                factory.Store.Categories.Add(new CategoryReplica(Category, Tenant, "Plomería", true, DateTimeOffset.UtcNow));
            }
        }

        factory.Geocoder.Result = Chapinero;
        factory.Localities.Result = new Locality("02", "Chapinero");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(Guid.NewGuid(), Tenant, role));
        return client;
    }

    private static object BodyWithoutLocation() => new
    {
        categoryId = Category,
        description = "Fuga de agua en la cocina",
        addressText = "Calle 63 # 9-45, apto 502",
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Crear_SinUbicacion_GeocodificaYResponde201()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(CreateUri, BodyWithoutLocation());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var location = (await Json(response)).GetProperty("location");
        Assert.Equal(4.6486, location.GetProperty("latitude").GetDouble());
        Assert.Equal(-74.063, location.GetProperty("longitude").GetDouble());
    }

    [Fact]
    public async Task Crear_SinUbicacionYNoEncontrada_Responde422DireccionNoEncontrada()
    {
        using var client = Client();
        factory.Geocoder.Result = null;

        using var response = await client.PostAsJsonAsync(CreateUri, BodyWithoutLocation());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var json = await Json(response);
        Assert.Equal("https://quickpatch.internal/problems/direccion-no-encontrada", json.GetProperty("type").GetString());
        Assert.True(json.TryGetProperty("correlationId", out _));
    }

    [Theory]
    [InlineData("cliente")]
    [InlineData("empresa_contacto")]
    public async Task VistaPrevia_RolesQueCreanSolicitudes_Responde200ConElContrato(string role)
    {
        using var client = Client(role);

        using var response = await client.PostAsJsonAsync(PreviewUri, new { addressText = "Calle 63 # 9-45, apto 502" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(4.6486, json.GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.Equal("02", json.GetProperty("locality").GetProperty("code").GetString());
        Assert.Equal("Chapinero", json.GetProperty("locality").GetProperty("name").GetString());
        Assert.Equal("Calle 63, Chapinero, Bogotá", json.GetProperty("matchedAddress").GetString());
    }

    [Fact]
    public async Task VistaPrevia_NoCreaNada()
    {
        using var client = Client();
        int before;
        lock (factory.Store)
        {
            before = factory.Store.ServiceRequests.Count;
        }

        using var response = await client.PostAsJsonAsync(PreviewUri, new { addressText = "Calle 63 # 9-45, apto 502" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        lock (factory.Store)
        {
            Assert.Equal(before, factory.Store.ServiceRequests.Count);
        }
    }

    [Theory]
    [InlineData("tecnico")]
    [InlineData("admin_tenant")]
    public async Task VistaPrevia_OtroRol_Responde403(string role)
    {
        using var client = Client(role);

        using var response = await client.PostAsJsonAsync(PreviewUri, new { addressText = "Calle 63 # 9-45" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task VistaPrevia_SinToken_Responde401()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(PreviewUri, new { addressText = "Calle 63 # 9-45" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task VistaPrevia_DireccionCorta_Responde400ConElCampo()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(PreviewUri, new { addressText = "Cl" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("addressText", out _));
    }

    [Fact]
    public async Task VistaPrevia_NoEncontrada_Responde422DireccionNoEncontrada()
    {
        using var client = Client();
        factory.Geocoder.Result = null;

        using var response = await client.PostAsJsonAsync(PreviewUri, new { addressText = "Avenida Inexistente # 1-1" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/direccion-no-encontrada", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task VistaPrevia_FueraDeLasLocalidades_Responde422FueraDeCobertura()
    {
        using var client = Client();
        factory.Localities.Result = null;

        using var response = await client.PostAsJsonAsync(PreviewUri, new { addressText = "Calle 63 # 9-45" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/ubicacion-fuera-de-cobertura", (await Json(response)).GetProperty("type").GetString());
    }
}