using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.UnitTests.Support;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

/// <summary><c>GET /v1/service-requests/{id}</c> con la calificación en el detalle (contrato 1.12.0, SCRUM-637 y SCRUM-32).</summary>
public class DetailRatingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private (DomainServiceRequest Request, Guid Client, Guid Technician) Seed(string status, int? score = null, string? comment = null)
    {
        var client = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var request = DomainServiceRequest.Restore(
            Guid.NewGuid(), Tenant, client, Guid.NewGuid(), technician, "Fuga de agua en la cocina", new GeoPoint(4.6486, -74.063),
            "Calle 63 # 9-45", status, Now, Now.AddMinutes(1));
        lock (factory.Store)
        {
            factory.Store.ServiceRequests.Add(request);
            factory.Directory.Users[client] = new UserContact(client, "Ana Gómez", "+573009876543");
            factory.Directory.Users[technician] = new UserContact(technician, "Carlos Pérez", "3101234567");
            if (score is { } value)
            {
                factory.Store.Ratings.Add(Rating.Create(Tenant, request.Id, technician, value, comment, Now.AddHours(2)));
            }
        }

        return (request, client, technician);
    }

    private HttpClient As(Guid user, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, Tenant, role));
        return client;
    }

    private static Uri Detail(Guid id) => new($"/v1/service-requests/{id}", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Cliente_DetalleCalificado_TraeElRatingConScoreCommentYRatedAt()
    {
        var (request, client, _) = Seed(ServiceRequestStatus.Pagado, 5, "Puntual y ordenado.");
        using var http = As(client, "cliente");

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rating = (await Json(response)).GetProperty("rating");
        Assert.Equal(["comment", "ratedAt", "score"], rating.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(5, rating.GetProperty("score").GetInt32());
        Assert.Equal("Puntual y ordenado.", rating.GetProperty("comment").GetString());
        Assert.Equal(Now.AddHours(2), rating.GetProperty("ratedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Cliente_DetalleSinCalificar_RatingEsNullPeroEstaPresente()
    {
        var (request, client, _) = Seed(ServiceRequestStatus.Pagado);
        using var http = As(client, "cliente");

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(response)).GetProperty("rating").ValueKind);
    }

    [Fact]
    public async Task CalificacionSinComentario_ComentarioNull()
    {
        var (request, client, _) = Seed(ServiceRequestStatus.Pagado, 3);
        using var http = As(client, "cliente");

        using var response = await http.GetAsync(Detail(request.Id));

        var rating = (await Json(response)).GetProperty("rating");
        Assert.Equal(3, rating.GetProperty("score").GetInt32());
        Assert.Equal(JsonValueKind.Null, rating.GetProperty("comment").ValueKind);
    }

    [Fact]
    public async Task TecnicoAsignado_ElDetalleNoTraeCalificacion()
    {
        var (request, _, technician) = Seed(ServiceRequestStatus.Completado, 4, "No debe verla el técnico.");
        using var http = As(technician, "tecnico");

        using var response = await http.GetAsync(Detail(request.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(response)).GetProperty("rating").ValueKind);
    }
}