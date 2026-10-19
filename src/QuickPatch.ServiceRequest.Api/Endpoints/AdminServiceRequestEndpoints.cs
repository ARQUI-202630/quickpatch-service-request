using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Esquema <c>AdminServiceRequest</c> (1.4.0): sin dirección, ubicación ni contacto (DD 8.3.15).</summary>
public sealed record AdminServiceRequestResponse(
    Guid Id,
    string Status,
    Guid CategoryId,
    Guid ClientId,
    Guid? TechnicianId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignedAt)
{
    public static AdminServiceRequestResponse From(DomainServiceRequest s) =>
        new(s.Id, s.Status, s.CategoryId, s.ClientId, s.TechnicianId, s.CreatedAt, s.AssignedAt);
}

/// <summary>Esquema <c>AdminServiceRequestPage</c> (1.4.0).</summary>
public sealed record AdminServiceRequestPageResponse(
    IReadOnlyList<AdminServiceRequestResponse> Items, int Page, int Size, int TotalItems);

/// <summary>Esquema <c>ServiceRequestSummary</c> (1.4.0). <c>byStatus</c> trae siempre los 9 estados.</summary>
public sealed record ServiceRequestSummaryResponse(DateTimeOffset GeneratedAt, IReadOnlyDictionary<string, int> ByStatus, int CreatedToday);

/// <summary>Esquema <c>TenantServiceRequestSummary</c> (1.4.0).</summary>
public sealed record TenantServiceRequestSummaryResponse(Guid TenantId, IReadOnlyDictionary<string, int> ByStatus, int CreatedToday);

/// <summary>Esquema <c>PlatformServiceRequestSummary</c> (1.4.0).</summary>
public sealed record PlatformServiceRequestSummaryResponse(DateTimeOffset GeneratedAt, IReadOnlyList<TenantServiceRequestSummaryResponse> Tenants);

/// <summary>
/// Panel operativo de solicitudes (RF-18, SCRUM-38; contrato 1.4.0, DD 8.3.15): lista y conteos del <c>admin_tenant</c>,
/// bajo RLS, y conteos de todos los tenants para el <c>admin_plataforma</c> (DD 10.4).
/// </summary>
public static partial class AdminServiceRequestEndpoints
{
    public const string AdminSummaryPath = "/v1/admin/service-requests/summary";
    public const string PlatformSummaryPath = "/v1/platform/service-requests/summary";

    public static IEndpointRouteBuilder MapAdminServiceRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ServiceRequestEndpoints.BasePath, ListForTenantAsync).WithName("listServiceRequests")
            .RequireAuthorization(AuthenticatedUser.AdminTenantPolicy);
        app.MapGet(AdminSummaryPath, GetTenantSummaryAsync).WithName("getServiceRequestSummary")
            .RequireAuthorization(AuthenticatedUser.AdminTenantPolicy);
        app.MapGet(PlatformSummaryPath, GetPlatformSummaryAsync).WithName("getPlatformServiceRequestSummary")
            .RequireAuthorization(AuthenticatedUser.PlatformAdminPolicy);
        return app;
    }

    private static async Task<IResult> ListForTenantAsync(
        HttpContext context,
        ListTenantServiceRequestsHandler handler,
        string? status,
        string? page,
        string? size,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthorized();
        }

        // page y size llegan como texto para responder 400 con el campo, igual que la lista del técnico.
        var errors = new Dictionary<string, string[]>();
        var pageNumber = ServiceRequestEndpoints.ParseOptionalInt(page, "page", errors);
        var pageSize = ServiceRequestEndpoints.ParseOptionalInt(size, "size", errors);
        if (errors.Count > 0)
        {
            return ServiceRequestEndpoints.ValidationProblem(errors);
        }

        try
        {
            var result = await handler.HandleAsync(
                new ListTenantServiceRequestsQuery(user.TenantId, status, pageNumber, pageSize), cancellationToken);
            return Results.Ok(new AdminServiceRequestPageResponse(
                result.Items.Select(AdminServiceRequestResponse.From).ToList(), result.Page, result.Size, result.TotalItems));
        }
        catch (DomainValidationException ex)
        {
            return ServiceRequestEndpoints.ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> GetTenantSummaryAsync(
        HttpContext context, GetTenantSummaryHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthorized();
        }

        var summary = await handler.HandleAsync(user.TenantId, cancellationToken);
        return Results.Ok(new ServiceRequestSummaryResponse(summary.GeneratedAt, summary.Counts.ByStatus, summary.Counts.CreatedToday));
    }

    private static async Task<IResult> GetPlatformSummaryAsync(
        HttpContext context,
        GetPlatformSummaryHandler handler,
        ILogger<GetPlatformSummaryHandler> logger,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthorized();
        }

        Guid? filter = null;
        if (tenantId is not null)
        {
            if (!Guid.TryParse(tenantId, out var parsed) || parsed == Guid.Empty)
            {
                return ServiceRequestEndpoints.ValidationProblem(
                    new Dictionary<string, string[]> { ["tenantId"] = ["Debe ser un UUID."] });
            }

            filter = parsed;
        }

        // DD 10.4: la lectura entre tenants queda en el log estructurado (el correlationId va en el scope de la petición).
        LogPlatformRead(logger, user.UserId, filter);

        var summary = await handler.HandleAsync(filter, cancellationToken);
        return Results.Ok(new PlatformServiceRequestSummaryResponse(
            summary.GeneratedAt,
            summary.Tenants
                .Select(t => new TenantServiceRequestSummaryResponse(t.TenantId, t.Counts.ByStatus, t.Counts.CreatedToday))
                .ToList()));
    }

    private static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Operación de plataforma: conteo de solicitudes por tenant por el usuario {UserId}, filtro de tenant {TenantFilter} (vacío: todos).")]
    private static partial void LogPlatformRead(ILogger logger, Guid userId, Guid? tenantFilter);
}