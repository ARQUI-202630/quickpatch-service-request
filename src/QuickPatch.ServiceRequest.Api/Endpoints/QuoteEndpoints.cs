using Microsoft.AspNetCore.Mvc;

using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.Quotes;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Quotes;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Cuerpo de <c>POST /v1/service-requests/{id}/quotes</c> (esquema <c>IssueQuote</c>, contrato 1.6.0).</summary>
public sealed record IssueQuoteRequest(string? LaborAmount, string? MaterialsAmount, string? Detail);

/// <summary>Cuerpo opcional de <c>POST .../quotes/{quoteId}/reject</c> (esquema <c>RejectQuote</c>, 1.6.0).</summary>
public sealed record RejectQuoteRequest(string? Reason);

/// <summary>Esquema <c>Quote</c> (1.6.0). Los montos van como texto con dos decimales.</summary>
public sealed record QuoteResponse(
    Guid Id,
    int QuoteNumber,
    string Status,
    string LaborAmount,
    string MaterialsAmount,
    string TotalAmount,
    string? Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RespondedAt,
    string? RejectionReason)
{
    public static QuoteResponse From(NumberedQuote numbered)
    {
        ArgumentNullException.ThrowIfNull(numbered);
        var q = numbered.Quote;
        return new(
            q.Id,
            numbered.QuoteNumber,
            q.Status,
            Quote.FormatMoney(q.LaborAmount),
            Quote.FormatMoney(q.MaterialsAmount),
            Quote.FormatMoney(q.TotalAmount),
            q.Detail,
            q.CreatedAt,
            q.ExpiresAt,
            q.RespondedAt,
            q.RejectionReason);
    }
}

/// <summary>Esquema <c>QuoteDecision</c> (1.6.0).</summary>
public sealed record QuoteDecisionResponse(Guid QuoteId, string QuoteStatus, string ServiceRequestStatus, int RemainingQuotes);

/// <summary>Cotizaciones de la solicitud (RF-34, RF-35; contrato <c>service-request.v1.yaml</c> 1.6.0).</summary>
public static class QuoteEndpoints
{
    public static IEndpointRouteBuilder MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ServiceRequestEndpoints.BasePath}/{{id:guid}}/quotes");
        group.MapGet("/", ListAsync).WithName("listQuotes").RequireAuthorization(AuthenticatedUser.ReadPolicy);
        group.MapPost("/", IssueAsync).WithName("issueQuote").RequireAuthorization(AuthenticatedUser.TechnicianPolicy);
        group.MapPost("/{quoteId:guid}/accept", AcceptAsync).WithName("acceptQuote").RequireAuthorization(AuthenticatedUser.ClientPolicy);
        group.MapPost("/{quoteId:guid}/reject", RejectAsync).WithName("rejectQuote").RequireAuthorization(AuthenticatedUser.ClientPolicy);
        return app;
    }

    private static async Task<IResult> ListAsync(Guid id, HttpContext context, ListQuotesHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthenticated();
        }

        var quotes = await handler.HandleAsync(
            new ListQuotesQuery(user.TenantId, user.UserId, user.Role == AuthenticatedUser.TechnicianRole, id), cancellationToken);
        return quotes is null ? NotFound() : Results.Ok(quotes.Select(QuoteResponse.From).ToList());
    }

    private static async Task<IResult> IssueAsync(
        Guid id, IssueQuoteRequest body, HttpContext context, IssueQuoteHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthenticated();
        }

        IssueQuoteResult result;
        try
        {
            result = await handler.HandleAsync(
                new IssueQuoteCommand(
                    user.TenantId, user.UserId, id, body.LaborAmount, body.MaterialsAmount, body.Detail, CorrelationId.Get(context)),
                cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ServiceRequestEndpoints.ValidationProblem(ex.Errors);
        }

        return result switch
        {
            IssueQuoteResult.Issued issued => Results.Created(
                $"{ServiceRequestEndpoints.BasePath}/{id}/quotes", QuoteResponse.From(issued.Quote)),
            IssueQuoteResult.InvalidTransition invalid => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.InvalidTransition,
                title: "La solicitud no admite una cotización",
                detail: $"La solicitud está en '{invalid.Status}' y la operación requiere 'asignado'."),
            IssueQuoteResult.LimitReached => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.QuoteLimitReached,
                title: "La solicitud ya tiene 3 cotizaciones"),
            _ => NotFound(),
        };
    }

    private static Task<IResult> AcceptAsync(
        Guid id, Guid quoteId, HttpContext context, RespondQuoteHandler handler, CancellationToken cancellationToken) =>
        RespondAsync(id, quoteId, accept: true, reason: null, context, handler, cancellationToken);

    private static Task<IResult> RejectAsync(
        Guid id,
        Guid quoteId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RejectQuoteRequest? body,
        HttpContext context,
        RespondQuoteHandler handler,
        CancellationToken cancellationToken) =>
        RespondAsync(id, quoteId, accept: false, body?.Reason, context, handler, cancellationToken);

    private static async Task<IResult> RespondAsync(
        Guid id, Guid quoteId, bool accept, string? reason, HttpContext context, RespondQuoteHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthenticated();
        }

        RespondQuoteResult result;
        try
        {
            result = await handler.HandleAsync(
                new RespondQuoteCommand(user.TenantId, user.UserId, id, quoteId, accept, reason, CorrelationId.Get(context)),
                cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ServiceRequestEndpoints.ValidationProblem(ex.Errors);
        }

        return result switch
        {
            RespondQuoteResult.Responded responded => Results.Ok(new QuoteDecisionResponse(
                responded.Quote.Id, responded.Quote.Status, responded.ServiceRequestStatus, responded.RemainingQuotes)),
            RespondQuoteResult.InvalidTransition => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.InvalidTransition,
                title: "La cotización ya no se puede responder",
                detail: "La cotización no está pendiente o ya venció."),
            _ => NotFound(),
        };
    }

    private static IResult Unauthenticated() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no existe.");
}