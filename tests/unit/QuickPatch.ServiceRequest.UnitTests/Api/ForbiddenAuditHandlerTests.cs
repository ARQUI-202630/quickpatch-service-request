using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Api.Security;

namespace QuickPatch.ServiceRequest.UnitTests.Api;

public class ForbiddenAuditHandlerTests
{
    [Fact]
    public async Task AccesoProhibido_SeRegistraComoWarning()
    {
        var logger = new ListLogger<ForbiddenAuditHandler>();
        var handler = new ForbiddenAuditHandler(logger);
        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication("prueba").AddScheme<AuthenticationSchemeOptions, NoopAuthHandler>("prueba", _ => { });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "POST";
        context.Request.Path = "/v1/service-requests";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1"), new Claim("role", "tecnico")], "test"));
        var policy = new AuthorizationPolicyBuilder().RequireRole("cliente").Build();

        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());
        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Success());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("/v1/service-requests", entry.Message, StringComparison.Ordinal);
        Assert.Contains("tecnico", entry.Message, StringComparison.Ordinal);
    }

    private sealed class NoopAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}