using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// Solicitud de empresa (RF-08, SCRUM-73) contra PostgreSQL real: <c>client_type</c>, <c>company_id</c> y
/// <c>site_name</c> quedan guardados con la empresa del token, la base rechaza combinaciones inválidas (RN-SR12) y otro
/// usuario de la empresa la ve bajo RLS (RN-SR14).
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class CompanyRequestFlowTests(ServiceRequestEnvironment env)
{
    private async Task<Guid> SeedCategoryAsync(Guid tenant)
    {
        var category = Guid.NewGuid();
        await env.ExecuteAdminAsync(
            "INSERT INTO service_request_categories (category_id, tenant_id, name, active, updated_at) " +
            $"VALUES ('{category}', '{tenant}', 'Aire acondicionado', true, now());");
        return category;
    }

    private HttpClient As(Guid user, Guid tenant, string role, Guid? company = null)
    {
        var client = env.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(user, tenant, role, companyId: company));
        return client;
    }

    [Fact]
    public async Task EmpresaContacto_CreaYOtroDeLaEmpresaLaVe()
    {
        var tenant = Guid.NewGuid();
        var company = Guid.NewGuid();
        var category = await SeedCategoryAsync(tenant);
        using var creador = As(Guid.NewGuid(), tenant, "empresa_contacto", company);
        using var colega = As(Guid.NewGuid(), tenant, "empresa_contacto", company);
        using var ajeno = As(Guid.NewGuid(), tenant, "empresa_contacto", Guid.NewGuid());

        using var alta = await creador.PostAsJsonAsync(new Uri("/v1/service-requests", UriKind.Relative), new
        {
            categoryId = category,
            description = "El aire acondicionado de la sala no enfría.",
            location = new { latitude = 4.6486, longitude = -74.063 },
            addressText = "Carrera 7 # 71-21, piso 8",
            siteName = "Sede Chapinero",
        });

        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var id = (await alta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("empresa", await env.ScalarAdminAsync<string>("SELECT client_type FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal(company, await env.ScalarAdminAsync<Guid>("SELECT company_id FROM service_requests WHERE id = @id", ("id", id)));
        Assert.Equal("Sede Chapinero", await env.ScalarAdminAsync<string>("SELECT site_name FROM service_requests WHERE id = @id", ("id", id)));

        using var visto = await colega.GetAsync(new Uri($"/v1/service-requests/{id}", UriKind.Relative));
        using var noVisto = await ajeno.GetAsync(new Uri($"/v1/service-requests/{id}", UriKind.Relative));
        var lista = await colega.GetFromJsonAsync<JsonElement>(new Uri("/v1/service-requests", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, visto.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noVisto.StatusCode);
        Assert.Equal(id, Assert.Single(lista.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // Con el mismo usuario en otro tenant, RLS no deja ver nada.
        using var otroTenant = As(Guid.NewGuid(), Guid.NewGuid(), "empresa_contacto", company);
        using var fuera = await otroTenant.GetAsync(new Uri($"/v1/service-requests/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, fuera.StatusCode);
    }

    [Fact]
    public async Task LasSolicitudesAnterioresQuedanDeHogar_YLaBaseExigeRnSr12()
    {
        var id = Guid.NewGuid();
        await env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, description, location, address_text, status, created_at) " +
            $"VALUES ('{id}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'Fuga de agua en la cocina', " +
            "ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', 'buscando_tecnico', now());");

        Assert.Equal("hogar", await env.ScalarAdminAsync<string>("SELECT client_type FROM service_requests WHERE id = @id", ("id", id)));

        var empresaSinId = await Assert.ThrowsAsync<PostgresException>(() =>
            env.ExecuteAdminAsync($"UPDATE service_requests SET client_type = 'empresa' WHERE id = '{id}'"));
        var sedeEnHogar = await Assert.ThrowsAsync<PostgresException>(() =>
            env.ExecuteAdminAsync($"UPDATE service_requests SET site_name = 'Sede' WHERE id = '{id}'"));

        Assert.Equal("ck_service_requests_client_type", empresaSinId.ConstraintName);
        Assert.Equal("ck_service_requests_site_name", sedeEnHogar.ConstraintName);
    }
}