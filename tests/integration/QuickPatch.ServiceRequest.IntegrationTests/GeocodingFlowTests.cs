using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-456 contra PostgreSQL/PostGIS real y un Nominatim falso por HTTP: la copia de las localidades (P-09), la vista
/// previa de la ubicación y crear una solicitud solo con la dirección (contrato 1.3.0, ADR-024).
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class GeocodingFlowTests(ServiceRequestEnvironment env)
{
    private static readonly Uri PreviewUri = new("/v1/service-requests/location-preview", UriKind.Relative);

    private HttpClient As(Guid tenant, string role = "cliente")
    {
        var client = env.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, role));
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Localidades_LasVeinteDeBogota_SoloLecturaParaElRolDelServicio()
    {
        Assert.Equal(20, await env.ScalarAdminAsync<long>("SELECT count(*) FROM localities"));
        Assert.Equal(0, await env.ScalarAdminAsync<long>("SELECT count(*) FROM localities WHERE NOT ST_IsValid(area)"));

        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using (var select = new NpgsqlCommand("SELECT count(*) FROM localities", connection))
        {
            // Sin tenant fijado: el catálogo es igual para todos y no tiene RLS.
            Assert.Equal(20L, await select.ExecuteScalarAsync());
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO localities (code, name, area) SELECT '99', 'Prueba', area FROM localities WHERE code = '02'", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task VistaPrevia_DireccionEnChapinero_DevuelveLaLocalidad()
    {
        env.Nominatim.Places["Calle 63 # 9-45, apto 502"] = (4.6486, -74.063, "Calle 63, Chapinero, Bogotá, Colombia");
        using var http = As(Guid.NewGuid());

        using var response = await http.PostAsJsonAsync(PreviewUri, new { addressText = "Calle 63 # 9-45, apto 502" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("02", json.GetProperty("locality").GetProperty("code").GetString());
        Assert.Equal("Chapinero", json.GetProperty("locality").GetProperty("name").GetString());
        Assert.Equal(4.6486, json.GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.Equal("Calle 63, Chapinero, Bogotá, Colombia", json.GetProperty("matchedAddress").GetString());
    }

    [Fact]
    public async Task VistaPrevia_SoloLaViaEncontrada_ReintentaYDevuelveElPunto()
    {
        env.Nominatim.Places["Avenida Caracas"] = (4.6235, -74.0712, "Avenida Caracas, Santa Fe, Bogotá, Colombia");
        using var http = As(Guid.NewGuid());

        using var response = await http.PostAsJsonAsync(PreviewUri, new { addressText = "Avenida Caracas # 26-50" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Avenida Caracas # 26-50", env.Nominatim.Queries);
        Assert.Contains("Avenida Caracas", env.Nominatim.Queries);
    }

    [Fact]
    public async Task VistaPrevia_PuntoEnElRectanguloPeroFueraDeBogota_Responde422FueraDeCobertura()
    {
        // La Calera: dentro del rectángulo de RN-SR9, fuera de las 20 localidades.
        env.Nominatim.Places["Vereda El Salitre, La Calera"] = (4.72, -73.98, "La Calera, Cundinamarca, Colombia");
        using var http = As(Guid.NewGuid());

        using var response = await http.PostAsJsonAsync(PreviewUri, new { addressText = "Vereda El Salitre, La Calera" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/ubicacion-fuera-de-cobertura", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Crear_ConGpsEnSoacha_Responde422FueraDeCobertura()
    {
        // SCRUM-636: Soacha queda dentro del rectángulo de RN-SR9 pero fuera de las 20 localidades.
        using var http = As(Guid.NewGuid());

        using var response = await http.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new
        {
            categoryId = Guid.NewGuid(),
            description = "Fuga de agua debajo del lavaplatos",
            location = new { latitude = 4.5794, longitude = -74.2168 },
            addressText = "Calle 13 # 7-20, Soacha",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/ubicacion-fuera-de-cobertura", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Crear_SoloConDireccionFueraDeLasLocalidades_RechazaIgualQueLaVistaPrevia()
    {
        // SCRUM-636: la misma dirección que la vista previa rechaza tampoco crea la solicitud.
        env.Nominatim.Places["Vereda El Salitre, La Calera"] = (4.72, -73.98, "La Calera, Cundinamarca, Colombia");
        using var http = As(Guid.NewGuid());

        using var response = await http.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new
        {
            categoryId = Guid.NewGuid(),
            description = "Fuga de agua debajo del lavaplatos",
            addressText = "Vereda El Salitre, La Calera",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/ubicacion-fuera-de-cobertura", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task VistaPrevia_NoEncontrada_Responde422DireccionNoEncontrada()
    {
        using var http = As(Guid.NewGuid());

        using var response = await http.PostAsJsonAsync(PreviewUri, new { addressText = "Calle que no existe # 1-1" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/direccion-no-encontrada", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Crear_SoloConLaDireccion_GuardaElPuntoGeocodificado()
    {
        var tenant = Guid.NewGuid();
        var category = Guid.NewGuid();
        await env.PublishAsync("catalog.category-changed", category.ToString(), JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "catalog.category-changed",
            eventVersion = 1,
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "catalog-service",
            data = new { categoryId = category, name = "Plomería", active = true, updatedAt = DateTimeOffset.UtcNow },
        }));
        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>("SELECT count(*) FROM service_request_categories WHERE category_id = @c", ("c", category)) == 1,
            "la réplica recibe la categoría");
        env.Nominatim.Places["Calle 72 # 10-34, oficina 301"] = (4.6575, -74.0563, "Calle 72, Chapinero, Bogotá, Colombia");
        using var http = As(tenant);

        using var response = await http.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new
        {
            categoryId = category,
            description = "Fuga de agua debajo del lavaplatos",
            addressText = "Calle 72 # 10-34, oficina 301",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await Json(response)).GetProperty("id").GetGuid();
        Assert.Equal(4.6575, await env.ScalarAdminAsync<double>("SELECT ST_Y(location) FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(-74.0563, await env.ScalarAdminAsync<double>("SELECT ST_X(location) FROM service_requests WHERE id = @id", ("id", id)));
    }
}