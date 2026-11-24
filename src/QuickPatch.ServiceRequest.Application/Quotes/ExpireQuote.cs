using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Quotes;

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