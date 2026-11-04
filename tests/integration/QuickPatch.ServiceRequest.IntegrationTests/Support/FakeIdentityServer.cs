using System.Collections.Concurrent;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace QuickPatch.ServiceRequest.IntegrationTests.Support;

/// <summary>
/// Identity falso por HTTP real que cumple <c>GET /internal/v1/users</c> (identity-internal 1.0.0). Guarda el
/// <c>Authorization</c> de cada llamada para comprobar que ServiceRequest reenvía el token del usuario.
/// </summary>
public sealed class FakeIdentityServer : IAsyncDisposable
{
    private WebApplication? app;

    public ConcurrentDictionary<Guid, (string FullName, string? Phone)> Users { get; } = new();

    public ConcurrentQueue<string> Authorizations { get; } = new();

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.MapGet("/internal/v1/users", (HttpRequest request, string[] ids) =>
        {
            Authorizations.Enqueue(request.Headers.Authorization.ToString());
            var items = ids.Select(Guid.Parse)
                .Where(Users.ContainsKey)
                .Select(id => new { id, fullName = Users[id].FullName, phone = Users[id].Phone });
            return Results.Ok(new { items });
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