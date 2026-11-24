using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Cuerpo de <c>POST /v1/service-requests/location-preview</c> (contrato 1.3.0).</summary>
public sealed record LocationPreviewRequest(string? AddressText);

/// <summary>Esquema <c>LocationPreview</c> (1.3.0).</summary>
public sealed record LocationPreviewResponse(LocationDto Location, LocalityDto Locality, string MatchedAddress);

public sealed record LocalityDto(string Code, string Name);

/// <summary>Vista previa de la ubicación de una dirección escrita (contrato 1.3.0).</summary>
public static class LocationPreviewEndpoints
{
    public static IEndpointRouteBuilder MapLocationPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost($"{Routes.ServiceRequests}/location-preview", PreviewLocationAsync).WithName("previewServiceRequestLocation")
            .RequireAuthorization(AuthenticatedUser.PreviewPolicy);
        return app;
    }

    private static async Task<IResult> PreviewLocationAsync(
        LocationPreviewRequest body,
        HttpContext context,
        PreviewLocationHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out _))
        {
            return ApiResults.InvalidToken();
        }

        PreviewLocationResult result;
        try
        {
            result = await handler.HandleAsync(body.AddressText, cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ApiResults.ValidationProblem(ex.Errors);
        }

        return result switch
        {
            PreviewLocationResult.Found found => Results.Ok(new LocationPreviewResponse(
                new LocationDto(found.Location.Latitude, found.Location.Longitude),
                new LocalityDto(found.Locality.Code, found.Locality.Name),
                found.MatchedAddress)),
            PreviewLocationResult.OutOfCoverage => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.OutOfCoverage,
                title: "La ubicación está fuera del área de cobertura",
                detail: "La dirección quedó fuera de Bogotá D.C. Comparte tu ubicación."),
            _ => ApiResults.AddressNotFound(),
        };
    }
}