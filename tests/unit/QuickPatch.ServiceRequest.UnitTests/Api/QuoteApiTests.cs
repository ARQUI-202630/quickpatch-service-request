using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary>Cotizaciones (contrato <c>service-request.v1.yaml</c> 1.6.0, RF-34, RF-35).</summary>
public class QuoteApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid client = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();

    private DomainServiceRequest Seed(string status = ServiceRequestStatus.Asignado)
    {
        var now = DateTimeOffset.UtcNow;
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), tenant, client, Guid.NewGuid(), technician, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45", status, now.AddHours(-1), now.AddMinutes(-30));
        lock (factory.Store)
        {
            factory.Store.ServiceRequests.Add(request);
        }

        return request;
    }

    private Quote SeedQuote(DomainServiceRequest request, string status = QuoteStatus.Pendiente)
    {
        var now = DateTimeOffset.UtcNow;
        var quote = Quote.Restore(
            Guid.NewGuid(), tenant, request.Id, technician, 120000m, 65000m, "Cambio de sifón.", status, now.AddMinutes(-5), now.AddHours(24));
        lock (factory.Store)
        {
            factory.Store.Quotes.Add(quote);
        }

        return quote;
    }

    private HttpClient As(Guid user, string role)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return http;
    }

    private static Uri Quotes(Guid id) => new($"/v1/service-requests/{id}/quotes", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Tecnico_Emite_201ConElEsquemaQuote()
    {
        var request = Seed();
        using var http = As(technician, "tecnico");

        using var response = await http.PostAsJsonAsync(Quotes(request.Id),
            new { laborAmount = "120000.00", materialsAmount = "65000.00", detail = "Cambio de sifón." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("pendiente", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("quoteNumber").GetInt32());
        Assert.Equal("120000.00", body.GetProperty("laborAmount").GetString());
        Assert.Equal("65000.00", body.GetProperty("materialsAmount").GetString());
        Assert.Equal("185000.00", body.GetProperty("totalAmount").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("respondedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("rejectionReason").ValueKind);
        Assert.Equal(ServiceRequestStatus.Cotizado, request.Status);
    }

    [Fact]
    public async Task MontoSinDecimales_400ConElCampo()
    {
        var request = Seed();
        using var http = As(technician, "tecnico");

        using var response = await http.PostAsJsonAsync(Quotes(request.Id), new { laborAmount = "120000", materialsAmount = "0.00" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("laborAmount", out _));
    }

    [Fact]
    public async Task Cliente_NoEmite_403_YTecnico_NoAcepta_403()
    {
        var request = Seed(ServiceRequestStatus.Cotizado);
        var quote = SeedQuote(request);
        using var asClient = As(client, "cliente");
        using var asTechnician = As(technician, "tecnico");

        using var issue = await asClient.PostAsJsonAsync(Quotes(request.Id), new { laborAmount = "1.00", materialsAmount = "0.00" });
        using var accept = await asTechnician.PostAsync(new Uri($"{Quotes(request.Id)}/{quote.Id}/accept", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
    }

    [Fact]
    public async Task ConCotizacionPendiente_409TransicionInvalida()
    {
        var request = Seed(ServiceRequestStatus.Cotizado);
        SeedQuote(request);
        using var http = As(technician, "tecnico");

        using var response = await http.PostAsJsonAsync(Quotes(request.Id), new { laborAmount = "1.00", materialsAmount = "0.00" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith("/problems/transicion-invalida", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task CuartaCotizacion_409LimiteDeCotizaciones()
    {
        var request = Seed();
        for (var i = 0; i < 3; i++)
        {
            SeedQuote(request, QuoteStatus.Rechazada);
        }

        using var http = As(technician, "tecnico");

        using var response = await http.PostAsJsonAsync(Quotes(request.Id), new { laborAmount = "1.00", materialsAmount = "0.00" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith("/problems/limite-de-cotizaciones", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Cliente_Acepta_200ConQuoteDecision()
    {
        var request = Seed(ServiceRequestStatus.Cotizado);
        var quote = SeedQuote(request);
        using var http = As(client, "cliente");

        using var response = await http.PostAsync(new Uri($"{Quotes(request.Id)}/{quote.Id}/accept", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(quote.Id, body.GetProperty("quoteId").GetGuid());
        Assert.Equal("aceptada", body.GetProperty("quoteStatus").GetString());
        Assert.Equal("cotizacion_aceptada", body.GetProperty("serviceRequestStatus").GetString());
        Assert.Equal(2, body.GetProperty("remainingQuotes").GetInt32());
    }

    [Fact]
    public async Task Cliente_RechazaSinCuerpo_200_YConMotivo_ElTecnicoLoVe()
    {
        var request = Seed(ServiceRequestStatus.Cotizado);
        var quote = SeedQuote(request);
        using var asClient = As(client, "cliente");

        using var response = await asClient.PostAsJsonAsync(
            new Uri($"{Quotes(request.Id)}/{quote.Id}/reject", UriKind.Relative), new { reason = "El valor es muy alto." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("asignado", (await Json(response)).GetProperty("serviceRequestStatus").GetString());

        using var asTechnician = As(technician, "tecnico");
        using var list = await asTechnician.GetAsync(Quotes(request.Id));
        var item = Assert.Single((await Json(list)).EnumerateArray());
        Assert.Equal("rechazada", item.GetProperty("status").GetString());
        Assert.Equal("El valor es muy alto.", item.GetProperty("rejectionReason").GetString());

        // Sin cuerpo también es válido (RejectQuote es opcional).
        var second = Seed(ServiceRequestStatus.Cotizado);
        var secondQuote = SeedQuote(second);
        using var empty = await asClient.PostAsync(new Uri($"{Quotes(second.Id)}/{secondQuote.Id}/reject", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
    }

    [Fact]
    public async Task OtroCliente_404_NoRevelaLaSolicitud()
    {
        var request = Seed(ServiceRequestStatus.Cotizado);
        var quote = SeedQuote(request);
        using var http = As(Guid.NewGuid(), "cliente");

        using var list = await http.GetAsync(Quotes(request.Id));
        using var accept = await http.PostAsync(new Uri($"{Quotes(request.Id)}/{quote.Id}/accept", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    }

    [Fact]
    public async Task CampoDesconocido_400()
    {
        var request = Seed();
        using var http = As(technician, "tecnico");

        using var response = await http.PostAsJsonAsync(Quotes(request.Id),
            new { laborAmount = "1.00", materialsAmount = "0.00", totalAmount = "999.00" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}