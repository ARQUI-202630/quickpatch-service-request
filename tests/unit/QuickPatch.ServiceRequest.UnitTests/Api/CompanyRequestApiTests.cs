using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>
/// Solicitud de empresa (RF-08, SCRUM-28; contrato 1.9.0): la crea el <c>empresa_contacto</c> con la empresa del token
/// (RN-SR12), la ven los demás usuarios de la empresa (RN-SR14) y la lista distingue el tipo de cliente.
/// </summary>
public class CompanyRequestApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri Requests = new("/v1/service-requests", UriKind.Relative);
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid category = Guid.NewGuid();
    private readonly Guid company = Guid.NewGuid();

    private HttpClient As(string role, Guid? user = null, Guid? companyId = null, Guid? tenantId = null)
    {
        lock (factory.Store)
        {
            if (!factory.Store.Categories.Any(c => c.CategoryId == category))
            {
                factory.Store.Categories.Add(new CategoryReplica(category, tenant, "Aire acondicionado", true, DateTimeOffset.UtcNow));
            }
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(user ?? Guid.NewGuid(), tenantId ?? tenant, role, companyId: companyId));
        return client;
    }

    private object Body(string? siteName = null) => new
    {
        categoryId = category,
        description = "El aire acondicionado de la sala no enfría.",
        location = new { latitude = 4.6486, longitude = -74.063 },
        addressText = "Carrera 7 # 71-21, piso 8",
        siteName,
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private DomainServiceRequest Seed(Guid client, Guid? companyId)
    {
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, client, category, null, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45", ServiceRequestStatus.BuscandoTecnico, DateTimeOffset.UtcNow, companyId: companyId,
            siteName: companyId is null ? null : "Sede Norte");
        lock (factory.Store)
        {
            factory.Store.ServiceRequests.Add(request);
        }

        return request;
    }

    [Fact]
    public async Task EmpresaContacto_Crea201_ConTipoEmpresaYLaEmpresaDelToken()
    {
        using var http = As("empresa_contacto", companyId: company);

        using var response = await http.PostAsJsonAsync(Requests, Body("  Sede Chapinero  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("empresa", json.GetProperty("clientType").GetString());
        Assert.Equal(company, json.GetProperty("companyId").GetGuid());
        Assert.Equal("Sede Chapinero", json.GetProperty("siteName").GetString());
        Assert.Equal("buscando_tecnico", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EmpresaContactoSinEmpresaEnElToken_403()
    {
        using var http = As("empresa_contacto");

        using var response = await http.PostAsJsonAsync(Requests, Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cliente_CreaDeHogar_YNoAdmiteSede()
    {
        using var http = As("cliente");

        using var hogar = await http.PostAsJsonAsync(Requests, Body());
        using var conSede = await http.PostAsJsonAsync(Requests, Body("Sede Norte"));

        Assert.Equal(HttpStatusCode.Created, hogar.StatusCode);
        var json = await Json(hogar);
        Assert.Equal("hogar", json.GetProperty("clientType").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("companyId").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, conSede.StatusCode);
        Assert.True((await Json(conSede)).GetProperty("errors").TryGetProperty("siteName", out _));
    }

    [Fact]
    public async Task SedeDeUnCaracter_400()
    {
        using var http = As("empresa_contacto", companyId: company);

        using var response = await http.PostAsJsonAsync(Requests, Body("A"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detalle_OtroUsuarioDeLaEmpresaLaVe_OtraEmpresaYElClienteNo()
    {
        var creator = Guid.NewGuid();
        var request = Seed(creator, company);
        using var peer = As("empresa_contacto", companyId: company);
        using var otherCompany = As("empresa_contacto", companyId: Guid.NewGuid());
        using var client = As("cliente");

        using var visto = await peer.GetAsync(new Uri($"/v1/service-requests/{request.Id}", UriKind.Relative));
        using var ajeno = await otherCompany.GetAsync(new Uri($"/v1/service-requests/{request.Id}", UriKind.Relative));
        using var cliente = await client.GetAsync(new Uri($"/v1/service-requests/{request.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, visto.StatusCode);
        Assert.Equal("Sede Norte", (await Json(visto)).GetProperty("siteName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, ajeno.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cliente.StatusCode);
    }

    [Fact]
    public async Task Lista_ClienteVeLasSuyas_EmpresaLasDeLaEmpresa_AdminTodasConElTipo()
    {
        var hogarUser = Guid.NewGuid();
        var hogar = Seed(hogarUser, null);
        Seed(Guid.NewGuid(), null);
        var deEmpresa = Seed(Guid.NewGuid(), company);
        var otraEmpresa = Seed(Guid.NewGuid(), Guid.NewGuid());
        var member = Guid.NewGuid();
        var propia = Seed(member, null);
        using var client = As("cliente", hogarUser);
        using var memberClient = As("empresa_contacto", member, company);
        using var admin = As("admin_tenant");

        var delCliente = (await Json(await client.GetAsync(Requests))).GetProperty("items").EnumerateArray().ToList();
        var deLaEmpresa = (await Json(await memberClient.GetAsync(Requests))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        var delAdmin = (await Json(await admin.GetAsync(new Uri("/v1/service-requests?size=100", UriKind.Relative))))
            .GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(hogar.Id, Assert.Single(delCliente).GetProperty("id").GetGuid());
        Assert.Equal("hogar", delCliente[0].GetProperty("clientType").GetString());
        Assert.Equal(new[] { deEmpresa.Id, propia.Id }.Order(), deLaEmpresa.Order());
        Assert.DoesNotContain(otraEmpresa.Id, deLaEmpresa);
        var item = delAdmin.Single(i => i.GetProperty("id").GetGuid() == deEmpresa.Id);
        Assert.Equal("empresa", item.GetProperty("clientType").GetString());
        Assert.Equal("Sede Norte", item.GetProperty("siteName").GetString());
        Assert.Equal(5, delAdmin.Count);
    }

    [Fact]
    public async Task OtroTenant_NoVeNada()
    {
        var request = Seed(Guid.NewGuid(), company);
        using var other = As("empresa_contacto", companyId: company, tenantId: Guid.NewGuid());

        using var detalle = await other.GetAsync(new Uri($"/v1/service-requests/{request.Id}", UriKind.Relative));
        using var lista = await other.GetAsync(Requests);

        Assert.Equal(HttpStatusCode.NotFound, detalle.StatusCode);
        Assert.Equal(0, (await Json(lista)).GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task ElCreadorEmpresaContacto_CancelaComoDueno()
    {
        var creator = Guid.NewGuid();
        var request = Seed(creator, company);
        using var owner = As("empresa_contacto", creator, company);
        using var peer = As("empresa_contacto", companyId: company);

        using var porOtro = await peer.PostAsJsonAsync(new Uri($"/v1/service-requests/{request.Id}/cancel", UriKind.Relative), new { reason = "Ya no se necesita." });
        using var porDueno = await owner.PostAsJsonAsync(new Uri($"/v1/service-requests/{request.Id}/cancel", UriKind.Relative), new { reason = "Ya no se necesita." });

        // Las acciones siguen siendo de quien la creó (RN-SR14).
        Assert.Equal(HttpStatusCode.NotFound, porOtro.StatusCode);
        Assert.Equal(HttpStatusCode.OK, porDueno.StatusCode);
    }
}