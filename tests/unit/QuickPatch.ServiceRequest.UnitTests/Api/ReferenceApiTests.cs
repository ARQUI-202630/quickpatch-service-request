using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;

using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>Con el tenant del canal configurado, como en QA (Channel__Tenants__quickpatch-mobile).</summary>
public sealed class ChannelApiFactory : ApiFactoryBase
{
    public static readonly Guid ChannelTenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    protected override void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Channel:TenantId", ChannelTenant.ToString());
        builder.UseSetting("Channel:Tenants:quickpatch-mobile", ChannelTenant.ToString());
    }
}

/// <summary>Categorías y localidades desde ServiceRequest (contrato 1.8.0, ADR-025).</summary>
public class ReferenceApiTests(ChannelApiFactory factory) : IClassFixture<ChannelApiFactory>
{
    private static readonly Uri Categories = new("/v1/service-requests/categories", UriKind.Relative);
    private static readonly Uri Localities = new("/v1/service-requests/localities", UriKind.Relative);

    private Guid Seed(Guid tenant, string name, bool active = true, string? description = "Fugas y tuberías.")
    {
        var id = Guid.NewGuid();
        lock (factory.Store)
        {
            factory.Store.Categories.Add(new CategoryReplica(id, tenant, name, active, DateTimeOffset.UtcNow, description));
        }

        return id;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ConToken_ActivasDelTenantDelToken_OrdenadasPorNombre_YSinInactivasNiAjenas()
    {
        var tenant = Guid.NewGuid();
        Seed(tenant, "Plomería");
        Seed(tenant, "Electricidad", description: null);
        Seed(tenant, "Gas", active: false);
        Seed(Guid.NewGuid(), "Cerrajería");
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid(), tenant, "cliente"));

        using var response = await http.GetAsync(Categories);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await Json(response)).EnumerateArray().ToList();
        Assert.Equal(["Electricidad", "Plomería"], items.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("description").ValueKind);
        Assert.Equal("Fugas y tuberías.", items[1].GetProperty("description").GetString());
        Assert.Equal(["id", "name", "description"], items[1].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task SinToken_LasDelTenantDelCanal()
    {
        var id = Seed(ChannelApiFactory.ChannelTenant, "Pintura");
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Channel-Id", "quickpatch-mobile");

        using var response = await http.GetAsync(Categories);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains((await Json(response)).EnumerateArray(), c => c.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task SinToken_UnCanalQueNoEstaEnElMapa_503()
    {
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Channel-Id", "otro-canal");

        using var response = await http.GetAsync(Categories);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task TokenInvalido_401_NuncaCaeAlCanal()
    {
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "no-es-un-jwt");
        http.DefaultRequestHeaders.Add("X-Channel-Id", "quickpatch-mobile");

        using var response = await http.GetAsync(Categories);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Localidades_SinToken_ConCodigoYNombre()
    {
        using var http = factory.CreateClient();

        using var response = await http.GetAsync(Localities);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await Json(response)).EnumerateArray().ToList();
        Assert.Equal("01", items[0].GetProperty("code").GetString());
        Assert.Equal("Chapinero", items[1].GetProperty("name").GetString());
    }
}