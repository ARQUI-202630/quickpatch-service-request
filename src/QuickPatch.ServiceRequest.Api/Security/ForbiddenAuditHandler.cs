using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace QuickPatch.ServiceRequest.Api.Security;

/// <summary>
/// Registra cada acceso rechazado por rol (RNF-04, TD IDN-016): log WARNING con ruta, usuario, rol y,
/// por el scope de la petición, el <c>correlationId</c>.
/// </summary>
public sealed partial class ForbiddenAuditHandler(ILogger<ForbiddenAuditHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Forbidden)
        {
            LogForbidden(
                logger,
                context.Request.Method,
                context.Request.Path,
                context.User.FindFirstValue("sub") ?? "-",
                context.User.FindFirstValue("role") ?? "-");
        }

        return fallback.HandleAsync(next, context, policy, authorizeResult);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acceso denegado (403): {Method} {Path} por el usuario {UserId} con rol {Role}.")]
    private static partial void LogForbidden(ILogger logger, string method, string path, string userId, string role);
}