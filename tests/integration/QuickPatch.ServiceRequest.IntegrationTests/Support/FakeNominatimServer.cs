using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace QuickPatch.ServiceRequest.IntegrationTests.Support;

/// <summary>
/// Nominatim falso por HTTP real que cumple <c>GET /search?format=jsonv2</c>: responde el punto registrado para el texto
/// de <c>q</c>, o una lista vacía. Guarda las consultas para comprobar qué se pidió.
/// </summary>
public sealed class FakeNominatimServer : IAsyncDisposable
{
    private WebApplication? app;

    public ConcurrentDictionary<string, (double Latitude, double Longitude, string DisplayName)> Places { get; } = new();

    public ConcurrentQueue<string> Queries { get; } = new();

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.MapGet("/search", (string q) =>
        {
            Queries.Enqueue(q);
            if (!Places.TryGetValue(q, out var place))
            {
                return Results.Ok(Array.Empty<object>());
            }

            return Results.Ok(new[]
            {
                new Dictionary<string, string>
                {
                    ["lat"] = place.Latitude.ToString(CultureInfo.InvariantCulture),
                    ["lon"] = place.Longitude.ToString(CultureInfo.InvariantCulture),
                    ["display_name"] = place.DisplayName,
                },
            });
        });
        await app.StartAsync();
        BaseUrl = app.Urls.Single();
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }
}