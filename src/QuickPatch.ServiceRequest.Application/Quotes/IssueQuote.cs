using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Quotes;

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