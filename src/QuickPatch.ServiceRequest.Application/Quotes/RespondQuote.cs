using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Quotes;

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