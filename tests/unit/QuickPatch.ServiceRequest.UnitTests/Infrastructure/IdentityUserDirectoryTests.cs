using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Infrastructure.Identity;

namespace QuickPatch.ServiceRequest.UnitTests.Infrastructure;

/// <summary>Cliente de la consulta interna por lote (identity-internal 1.0.0) con un servidor falso.</summary>
public class IdentityUserDirectoryTests
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Carlos = Guid.NewGuid();

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (IdentityUserDirectory Directory, FakeHandler Handler, HttpClient Http) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, bool withBaseUrl = true, TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        if (withBaseUrl)
        {
            http.BaseAddress = new Uri("http://identity.quickpatch.svc.cluster.local:8080/");
        }

        if (timeout is { } t)
        {
            http.Timeout = t;
        }

        return (new IdentityUserDirectory(http, NullLogger<IdentityUserDirectory>.Instance), handler, http);
    }

    [Fact]
    public async Task ReenviaTokenYCorrelacion_YLeeLosUsuarios()
    {
        var correlation = Guid.NewGuid();
        var (directory, handler, http) = Create((_, _) => Task.FromResult(Json(
            $$"""{"items":[{"id":"{{Ana}}","fullName":"Ana Gómez","phone":"+573009876543"},{"id":"{{Carlos}}","fullName":"Carlos Pérez","phone":null}]}""")));
        using (http)
        {
            var result = await directory.LookupAsync([Ana, Carlos, Ana], "token-del-usuario", correlation, CancellationToken.None);

            Assert.Equal("Ana Gómez", result[Ana].FullName);
            Assert.Equal("+573009876543", result[Ana].Phone);
            Assert.Null(result[Carlos].Phone);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/internal/v1/users?ids={Ana}&ids={Carlos}", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("token-del-usuario", request.Headers.Authorization.Parameter);
            Assert.Equal(correlation.ToString(), request.Headers.GetValues("X-Correlation-Id").Single());
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RespuestaDeError_DevuelveVacio(HttpStatusCode status)
    {
        var (directory, _, http) = Create((_, _) => Task.FromResult(Json("{}", status)));
        using (http)
        {
            Assert.Empty(await directory.LookupAsync([Ana], "t", Guid.NewGuid(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task CuerpoInvalido_DevuelveVacio()
    {
        var (directory, _, http) = Create((_, _) => Task.FromResult(Json("no es json")));
        using (http)
        {
            Assert.Empty(await directory.LookupAsync([Ana], "t", Guid.NewGuid(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task IdentityCaido_DevuelveVacio()
    {
        var (directory, _, http) = Create((_, _) => throw new HttpRequestException("conexión rechazada"));
        using (http)
        {
            Assert.Empty(await directory.LookupAsync([Ana], "t", Guid.NewGuid(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task TiempoDeEsperaVencido_DevuelveVacio()
    {
        var (directory, _, http) = Create(
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return Json("{}");
            },
            timeout: TimeSpan.FromMilliseconds(100));
        using (http)
        {
            Assert.Empty(await directory.LookupAsync([Ana], "t", Guid.NewGuid(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task CancelacionDelLlamador_SePropaga()
    {
        using var cts = new CancellationTokenSource();
        var (directory, _, http) = Create(
            async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return Json("{}");
            });
        using (http)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.LookupAsync([Ana], "t", Guid.NewGuid(), cts.Token));
        }
    }

    [Fact]
    public async Task SinDireccionSinIdsOSinToken_NoLlamaAIdentity()
    {
        var (sinDireccion, h1, http1) = Create((_, _) => Task.FromResult(Json("{}")), withBaseUrl: false);
        var (directory, h2, http2) = Create((_, _) => Task.FromResult(Json("{}")));
        using (http1)
        using (http2)
        {
            Assert.Empty(await sinDireccion.LookupAsync([Ana], "t", Guid.NewGuid(), CancellationToken.None));
            Assert.Empty(await directory.LookupAsync([], "t", Guid.NewGuid(), CancellationToken.None));
            Assert.Empty(await directory.LookupAsync([Ana], string.Empty, Guid.NewGuid(), CancellationToken.None));
            Assert.Empty(h1.Requests);
            Assert.Empty(h2.Requests);
        }
    }
}