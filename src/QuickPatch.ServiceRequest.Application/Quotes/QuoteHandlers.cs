using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Quotes;

/// <summary>Cotización con su posición en la solicitud (1 a 3, RN-Q7), como la devuelve el contrato 1.6.0.</summary>
public sealed record NumberedQuote(Quote Quote, int QuoteNumber);

/// <summary>Plazo de las cotizaciones (RN-Q8, DD v5.5: <c>Quotes__Ttl</c>).</summary>
public sealed record QuoteSettings(TimeSpan Ttl);

/// <summary><c>POST /v1/service-requests/{id}/quotes</c> (contrato 1.6.0). El técnico y el tenant salen del JWT.</summary>
public sealed record IssueQuoteCommand(
    Guid TenantId, Guid TechnicianId, Guid ServiceRequestId, string? LaborAmount, string? MaterialsAmount, string? Detail,
    Guid CorrelationId);

public abstract record IssueQuoteResult
{
    public sealed record Issued(NumberedQuote Quote) : IssueQuoteResult;

    /// <summary>No existe, es de otro tenant (RLS) o está asignada a otro técnico: 404 para no revelarla.</summary>
    public sealed record NotFound : IssueQuoteResult;

    /// <summary>La solicitud no está en <c>asignado</c> (RN-Q1, RN-Q2).</summary>
    public sealed record InvalidTransition(string Status) : IssueQuoteResult;

    /// <summary>La solicitud ya tiene 3 cotizaciones (RN-Q7).</summary>
    public sealed record LimitReached : IssueQuoteResult;
}

/// <summary>
/// El técnico asignado emite una cotización (RF-34, DD 8.3.7). En una transacción bajo el tenant del token bloquea la fila de
/// la solicitud, comprueba que esté en <c>asignado</c> y que no tenga ya 3 cotizaciones, guarda la cotización
/// <c>pendiente</c>, pasa la solicitud a <c>cotizado</c> y deja en el Outbox <c>service-request.quoted</c> y
/// <c>service-request.status-changed</c>.
/// </summary>
public sealed class IssueQuoteHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IQuoteRepository quotes,
    IOutbox outbox,
    QuoteSettings settings,
    TimeProvider clock)
{
    public async Task<IssueQuoteResult> HandleAsync(IssueQuoteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow();

        // Primero los montos: una petición inválida no abre transacción ni bloquea la fila.
        var quote = Quote.Issue(
            command.TenantId, command.ServiceRequestId, command.TechnicianId, command.LaborAmount, command.MaterialsAmount,
            command.Detail, now, settings.Ttl);

        return await unitOfWork.ExecuteAsync<IssueQuoteResult>(
            command.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                if (request is null || request.TechnicianId != command.TechnicianId)
                {
                    return new IssueQuoteResult.NotFound();
                }

                var existing = await quotes.ListAsync(request.Id, ct);
                if (existing.Count >= Quote.MaxPerServiceRequest)
                {
                    return new IssueQuoteResult.LimitReached();
                }

                var previousStatus = request.Status;
                if (!request.TryMarkQuoted())
                {
                    return new IssueQuoteResult.InvalidTransition(request.Status);
                }

                var number = existing.Count + 1;
                quotes.Add(quote);
                outbox.Enqueue(ServiceRequestEvents.Quoted(request, quote, number, command.CorrelationId, now));
                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, command.CorrelationId, now));
                return new IssueQuoteResult.Issued(new NumberedQuote(quote, number));
            },
            cancellationToken);
    }
}

/// <summary>Quien consulta las cotizaciones: el cliente dueño o el técnico asignado (contrato 1.6.0).</summary>
public sealed record ListQuotesQuery(Guid TenantId, Guid UserId, bool IsTechnician, Guid ServiceRequestId);

/// <summary>
/// Cotizaciones de la solicitud, de la más antigua a la más reciente (RF-34, RF-35). Null si la solicitud no existe, es de
/// otro tenant o no es del usuario (ni su cliente ni su técnico asignado): la API responde 404.
/// </summary>
public sealed class ListQuotesHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, IQuoteRepository quotes)
{
    public async Task<IReadOnlyList<NumberedQuote>?> HandleAsync(ListQuotesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await unitOfWork.ExecuteAsync<IReadOnlyList<NumberedQuote>?>(
            query.TenantId,
            async ct =>
            {
                var request = query.IsTechnician
                    ? await serviceRequests.FindForTechnicianAsync(query.ServiceRequestId, query.UserId, ct)
                    : await serviceRequests.FindForClientAsync(query.ServiceRequestId, query.UserId, ct);
                if (request is null)
                {
                    return null;
                }

                var list = await quotes.ListAsync(request.Id, ct);
                return list.Select((q, i) => new NumberedQuote(q, i + 1)).ToList();
            },
            cancellationToken);
    }
}

/// <summary><c>POST .../quotes/{quoteId}/accept</c> o <c>/reject</c> (contrato 1.6.0). El cliente y el tenant salen del JWT.</summary>
public sealed record RespondQuoteCommand(
    Guid TenantId, Guid ClientId, Guid ServiceRequestId, Guid QuoteId, bool Accept, string? Reason, Guid CorrelationId);

public abstract record RespondQuoteResult
{
    /// <summary>Esquema <c>QuoteDecision</c>: la cotización, el estado de la solicitud y las cotizaciones que quedan.</summary>
    public sealed record Responded(Quote Quote, string ServiceRequestStatus, int RemainingQuotes) : RespondQuoteResult;

    /// <summary>La solicitud no existe, es de otro tenant o de otro cliente, o la cotización no es de esa solicitud.</summary>
    public sealed record NotFound : RespondQuoteResult;

    /// <summary>La cotización no está pendiente o ya venció.</summary>
    public sealed record InvalidTransition : RespondQuoteResult;
}

/// <summary>
/// El cliente acepta o rechaza la cotización pendiente (RF-35, DD 8.3.8), con la fila de la solicitud bloqueada para que
/// no se cruce con el vencimiento ni con la cancelación.
/// <list type="bullet">
/// <item>Aceptar: la cotización queda <c>aceptada</c> y la solicitud pasa a <c>cotizacion_aceptada</c> (RN-Q4, RN-SR6);
/// Outbox: <c>quote-accepted</c> y <c>status-changed</c>.</item>
/// <item>Rechazar: la cotización queda <c>rechazada</c> con el motivo y la solicitud vuelve a <c>asignado</c>; Outbox:
/// <c>quote-rejected</c> y <c>status-changed</c>. Con la tercera, la solicitud se cancela por el sistema con
/// «Cotizaciones rechazadas» (RN-Q7) y además sale <c>service-request.cancelled</c>, para que Matching libere al técnico.</item>
/// </list>
/// </summary>
public sealed class RespondQuoteHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IQuoteRepository quotes,
    IOutbox outbox,
    TimeProvider clock)
{
    public async Task<RespondQuoteResult> HandleAsync(RespondQuoteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = command.Accept ? null : Quote.ValidateRejectionReason(command.Reason);

        return await unitOfWork.ExecuteAsync<RespondQuoteResult>(
            command.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                if (request is null || request.ClientId != command.ClientId)
                {
                    return new RespondQuoteResult.NotFound();
                }

                var list = await quotes.ListAsync(request.Id, ct);
                var quote = list.SingleOrDefault(q => q.Id == command.QuoteId);
                if (quote is null)
                {
                    return new RespondQuoteResult.NotFound();
                }

                var now = clock.GetUtcNow();
                var remaining = Quote.MaxPerServiceRequest - list.Count;
                var previousStatus = request.Status;
                if (command.Accept)
                {
                    if (request.Status != ServiceRequestStatus.Cotizado || !quote.TryAccept(now))
                    {
                        return new RespondQuoteResult.InvalidTransition();
                    }

                    request.TryAcceptQuote();
                    outbox.Enqueue(ServiceRequestEvents.QuoteAccepted(request, quote, command.CorrelationId, now));
                    outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, command.CorrelationId, now));
                    return new RespondQuoteResult.Responded(quote, request.Status, remaining);
                }

                if (request.Status != ServiceRequestStatus.Cotizado || !quote.TryReject(reason, now))
                {
                    return new RespondQuoteResult.InvalidTransition();
                }

                outbox.Enqueue(ServiceRequestEvents.QuoteRejected(request, quote, remaining, command.CorrelationId, now));
                if (remaining > 0)
                {
                    request.TryReturnToAssigned();
                }
                else
                {
                    request.TryCancel(null, CancellationActor.Sistema, Quote.RejectedCancellationReason, now);
                    outbox.Enqueue(ServiceRequestEvents.Cancelled(request, command.CorrelationId, now));
                }

                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, command.CorrelationId, now));
                return new RespondQuoteResult.Responded(quote, request.Status, remaining);
            },
            cancellationToken);
    }
}

/// <summary>
/// Vence una cotización sin respuesta (RN-Q6): la cotización pasa a <c>expirada</c> y la solicitud se cancela por el
/// sistema con «Cotización sin respuesta», lo que libera al técnico (RN-M6). Corre en el tenant de la cotización, con la
/// fila de la solicitud bloqueada. Es idempotente: si el cliente respondió, la solicitud se canceló o la cotización ya
/// venció en otra vuelta, no hace nada y devuelve false.
/// </summary>
public sealed class ExpireQuoteHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IQuoteRepository quotes,
    IOutbox outbox,
    TimeProvider clock)
{
    public async Task<bool> HandleAsync(ExpiredQuoteRef expired, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expired);
        return await unitOfWork.ExecuteAsync(
            expired.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(expired.ServiceRequestId, ct);
                if (request is null)
                {
                    return false;
                }

                var quote = (await quotes.ListAsync(request.Id, ct)).SingleOrDefault(q => q.Id == expired.QuoteId);
                var now = clock.GetUtcNow();
                if (quote is null || request.Status != ServiceRequestStatus.Cotizado || !quote.TryExpire(now))
                {
                    return false;
                }

                var previousStatus = request.Status;
                request.TryCancel(null, CancellationActor.Sistema, Quote.ExpiredCancellationReason, now);

                // Sin la petición de un usuario, el correlationId es el de la cotización que venció.
                outbox.Enqueue(ServiceRequestEvents.Cancelled(request, quote.Id, now));
                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, quote.Id, now));
                return true;
            },
            cancellationToken);
    }
}

/// <summary>Una vuelta del vencimiento: las cotizaciones vencidas de todos los tenants, de a <paramref name="limit"/>.</summary>
public sealed class ExpireQuotesBatch(IExpiredQuoteFinder finder, ExpireQuoteHandler expire, TimeProvider clock)
{
    /// <summary>Devuelve cuántas cotizaciones venció.</summary>
    public async Task<int> RunAsync(int limit, CancellationToken cancellationToken)
    {
        var expired = 0;
        foreach (var candidate in await finder.FindAsync(clock.GetUtcNow(), limit, cancellationToken))
        {
            if (await expire.HandleAsync(candidate, cancellationToken))
            {
                expired++;
            }
        }

        return expired;
    }
}