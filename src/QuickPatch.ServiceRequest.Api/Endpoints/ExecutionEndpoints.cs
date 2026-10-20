using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.Execution;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Evidence;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Esquema <c>StartedServiceRequest</c> (contrato 1.7.0).</summary>
public sealed record StartedServiceRequestResponse(Guid Id, string Status, DateTimeOffset StartedAt);

/// <summary>Esquema <c>CompletedServiceRequest</c> (1.7.0).</summary>
public sealed record CompletedServiceRequestResponse(Guid Id, string Status, DateTimeOffset CompletedAt);

/// <summary>Esquema <c>Evidence</c> (1.7.0): sin la clave del objeto en Garage (RN-E3).</summary>
public sealed record EvidenceResponse(Guid Id, DateTimeOffset UploadedAt)
{
    public static EvidenceResponse From(ServiceEvidence e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.Id, e.UploadedAt);
    }
}

/// <summary>Esquema <c>EvidenceList</c> (1.7.0).</summary>
public sealed record EvidenceListResponse(IReadOnlyList<EvidenceResponse> Items);

/// <summary>
/// Ejecución del servicio con evidencia (RF-14, RF-15; contrato <c>service-request.v1.yaml</c> 1.7.0): iniciar, subir y
/// consultar fotos, y completar.
/// </summary>
public static class ExecutionEndpoints
{
    /// <summary>Campo del formulario con la foto.</summary>
    public const string FileField = "file";

    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ServiceRequestEndpoints.BasePath}/{{id:guid}}");
        group.MapPost("/start", StartAsync).WithName("startService").RequireAuthorization(AuthenticatedUser.TechnicianPolicy);
        group.MapPost("/complete", CompleteAsync).WithName("completeService").RequireAuthorization(AuthenticatedUser.TechnicianPolicy);
        group.MapPost("/evidence", UploadAsync).WithName("uploadEvidence").RequireAuthorization(AuthenticatedUser.TechnicianPolicy);
        group.MapGet("/evidence", ListAsync).WithName("listEvidence").RequireAuthorization(AuthenticatedUser.EvidenceReadPolicy);
        group.MapGet("/evidence/{evidenceId:guid}/file", DownloadAsync).WithName("downloadEvidence")
            .RequireAuthorization(AuthenticatedUser.EvidenceReadPolicy);
        return app;
    }

    private static async Task<IResult> StartAsync(Guid id, HttpContext context, StartServiceHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthenticated();
        }

        var result = await handler.HandleAsync(
            new ServiceStepCommand(user.TenantId, user.UserId, id, CorrelationId.Get(context)), cancellationToken);
        return result switch
        {
            ServiceStepResult.Done done => Results.Ok(
                new StartedServiceRequestResponse(done.ServiceRequest.Id, done.ServiceRequest.Status, done.ServiceRequest.StartedAt!.Value)),
            ServiceStepResult.InvalidTransition invalid => InvalidTransition(
                "El servicio no se puede iniciar", invalid.Status, "cotizacion_aceptada"),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> CompleteAsync(
        Guid id, HttpContext context, CompleteServiceHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthenticated();
        }

        var result = await handler.HandleAsync(
            new ServiceStepCommand(user.TenantId, user.UserId, id, CorrelationId.Get(context)), cancellationToken);
        return result switch
        {
            ServiceStepResult.Done done => Results.Ok(new CompletedServiceRequestResponse(
                done.ServiceRequest.Id, done.ServiceRequest.Status, done.ServiceRequest.CompletedAt!.Value)),
            ServiceStepResult.InvalidTransition invalid => InvalidTransition(
                "El servicio no se puede completar", invalid.Status, "en_progreso"),
            ServiceStepResult.EvidenceRequired => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.EvidenceRequired,
                title: "Sube al menos una foto del trabajo"),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> UploadAsync(
        Guid id, HttpContext context, UploadEvidenceHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthenticated();
        }

        var content = await ReadFileAsync(context.Request, cancellationToken);
        if (content.Error is not null)
        {
            return ServiceRequestEndpoints.ValidationProblem(new Dictionary<string, string[]> { [FileField] = [content.Error] });
        }

        UploadEvidenceResult result;
        try
        {
            result = await handler.HandleAsync(new UploadEvidenceCommand(user.TenantId, user.UserId, id, content.Bytes), cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ServiceRequestEndpoints.ValidationProblem(ex.Errors);
        }

        return result switch
        {
            UploadEvidenceResult.Uploaded uploaded => Results.Created(
                $"{ServiceRequestEndpoints.BasePath}/{id}/evidence/{uploaded.Evidence.Id}/file", EvidenceResponse.From(uploaded.Evidence)),
            UploadEvidenceResult.InvalidTransition invalid => InvalidTransition(
                "La solicitud no admite evidencias", invalid.Status, "en_progreso"),
            UploadEvidenceResult.LimitReached => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.EvidenceLimitReached,
                title: $"La solicitud ya tiene {ServiceEvidence.MaxPerServiceRequest} evidencias"),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> ListAsync(Guid id, HttpContext context, ListEvidenceHandler handler, CancellationToken cancellationToken)
    {
        if (!TryQuery(context, id, out var query))
        {
            return Unauthenticated();
        }

        var items = await handler.HandleAsync(query, cancellationToken);
        return items is null ? NotFound() : Results.Ok(new EvidenceListResponse(items.Select(EvidenceResponse.From).ToList()));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id, Guid evidenceId, HttpContext context, DownloadEvidenceHandler handler, CancellationToken cancellationToken)
    {
        if (!TryQuery(context, id, out var query))
        {
            return Unauthenticated();
        }

        var file = await handler.HandleAsync(query, evidenceId, cancellationToken);
        if (file is null)
        {
            return NotFound();
        }

        // El archivo de una evidencia no cambia; solo lo guarda el dispositivo de quien lo pidió.
        context.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.Stream(file.Content, file.ContentType);
    }

    private static bool TryQuery(HttpContext context, Guid id, out EvidenceQuery query)
    {
        query = null!;
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return false;
        }

        var viewer = user.Role switch
        {
            AuthenticatedUser.TechnicianRole => EvidenceViewer.Technician,
            AuthenticatedUser.AdminTenantRole => EvidenceViewer.AdminTenant,
            _ => EvidenceViewer.Client,
        };
        query = new EvidenceQuery(user.TenantId, user.UserId, viewer, id);
        return true;
    }

    /// <summary>
    /// Lee el campo <c>file</c> del formulario. Un archivo que ya declara más de 10 MB se rechaza sin leerlo; el tipo lo valida
    /// el caso de uso por el contenido.
    /// </summary>
    private static async Task<(byte[] Bytes, string? Error)> ReadFileAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        const string Missing = "Adjunta una foto en el campo file (multipart/form-data).";
        if (!request.HasFormContentType)
        {
            return ([], Missing);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return ([], Missing);
        }

        var file = form.Files.GetFile(FileField);
        if (file is null)
        {
            return ([], Missing);
        }

        if (file.Length > ServiceEvidence.MaxBytes)
        {
            return ([], "La foto no puede pesar más de 10 MB.");
        }

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        return (buffer.ToArray(), null);
    }

    private static IResult InvalidTransition(string title, string status, string required) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: Problems.InvalidTransition,
            title: title,
            detail: $"La solicitud está en '{status}' y la operación requiere '{required}'.");

    private static IResult Unauthenticated() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no existe.");
}