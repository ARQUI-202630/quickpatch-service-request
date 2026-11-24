using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Cuerpo de <c>POST /v1/service-requests</c> (contrato <c>service-request.v1.yaml</c>).</summary>
public sealed record CreateServiceRequestRequest(Guid? CategoryId, string? Description, LocationDto? Location, string? AddressText, string? SiteName = null);

public sealed record LocationDto(double? Latitude, double? Longitude);

/// <summary>Esquema <c>ServiceRequestRating</c> del contrato (1.12.0): la calificación que el cliente dio al servicio pagado.</summary>
public sealed record ServiceRequestRatingResponse(int Score, string? Comment, DateTimeOffset RatedAt)
{
    public static ServiceRequestRatingResponse? From(Rating? rating) => rating is null ? null : new(rating.Score, rating.Comment, rating.CreatedAt);
}

/// <summary>
/// Esquema <c>ServiceRequest</c> del contrato (1.1.0: <c>assignedAt</c>, <c>technician</c> y <c>client</c>; 1.5.0: la cancelación;
/// 1.7.0: <c>startedAt</c> y <c>completedAt</c>; 1.12.0: <c>rating</c>, null mientras la solicitud no se califique).
/// </summary>
public sealed record ServiceRequestResponse(
    Guid Id,
    string Status,
    Guid CategoryId,
    Guid? TechnicianId,
    string Description,
    LocationDto Location,
    string AddressText,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignedAt,
    TechnicianView? Technician,
    ClientView? Client,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string ClientType,
    Guid? CompanyId,
    string? SiteName,
    ServiceRequestRatingResponse? Rating)
{
    public static ServiceRequestResponse From(
        DomainServiceRequest s, TechnicianView? technician = null, ClientView? client = null, Rating? rating = null) => new(
        s.Id,
        s.Status,
        s.CategoryId,
        s.TechnicianId,
        s.Description,
        new LocationDto(s.Location.Latitude, s.Location.Longitude),
        s.AddressText,
        s.CreatedAt,
        s.AssignedAt,
        technician,
        client,
        s.CancelledAt,
        s.CancellationReason,
        s.StartedAt,
        s.CompletedAt,
        s.ClientType,
        s.CompanyId,
        s.SiteName,
        ServiceRequestRatingResponse.From(rating));
}