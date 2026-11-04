using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.References;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Esquema <c>CategoryReference</c> (contrato 1.8.0): igual a <c>Category</c> de Catalog.</summary>
public sealed record CategoryReferenceResponse(Guid Id, string Name, string? Description);

/// <summary>
/// Catálogos de referencia para crear una solicitud (contrato <c>service-request.v1.yaml</c> 1.8.0, ADR-025): categorías y
/// localidades, servidas desde las copias de ServiceRequest.
/// </summary>
public static class ReferenceEndpoints
{
    public const string ChannelHeader = "X-Channel-Id";

    public static IEndpointRouteBuilder MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        // Con o sin token: sin token, el registro de técnicos ve las del tenant del canal (RF-02).
        app.MapGet($"{ServiceRequestEndpoints.BasePath}/categories", ListCategoriesAsync)
            .WithName("listServiceRequestCategories").AllowAnonymous();
        app.MapGet($"{ServiceRequestEndpoints.BasePath}/localities", ListLocalitiesAsync)
            .WithName("listServiceRequestLocalities").AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ListCategoriesAsync(
        HttpContext context, ListCategoriesHandler handler, IOptions<ChannelOptions> channel, CancellationToken cancellationToken)
    {
        Guid tenantId;
        if (context.Request.Headers.Authorization.Count > 0)
        {
            // Con token, el tenant sale del token. Un token inválido o vencido responde 401: nunca cae al canal.
            if (!AuthenticatedUser.TryFrom(context.User, out var user))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no es válido.");
            }

            tenantId = user.TenantId;
        }
        else if (channel.Value.TryResolve(context.Request.Headers[ChannelHeader].ToString(), out var channelTenant))
        {
            // Sin token, el tenant del canal según la configuración del servicio; nunca un tenant de la petición (RN-U5).
            tenantId = channelTenant;
        }
        else
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "El canal no tiene tenant configurado.");
        }

        var categories = await handler.HandleAsync(tenantId, cancellationToken);
        return Results.Ok(categories.Select(c => new CategoryReferenceResponse(c.CategoryId, c.Name, c.Description)).ToList());
    }

    private static async Task<IResult> ListLocalitiesAsync(ListLocalitiesHandler handler, CancellationToken cancellationToken) =>
        Results.Ok(await handler.HandleAsync(cancellationToken));
}