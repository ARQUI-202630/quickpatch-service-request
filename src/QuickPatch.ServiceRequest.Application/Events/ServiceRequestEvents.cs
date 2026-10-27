using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Quotes;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Events;

/// <summary>
/// Mensajes del Outbox de la solicitud (ADR-007), con el sobre común (DD 8.2.1) y la solicitud como clave. Cada caso de uso
/// los deja en la misma transacción que el cambio.
/// </summary>
public static class ServiceRequestEvents
{
    public static OutboxMessage StatusChanged(DomainServiceRequest request, string previousStatus, Guid correlationId, DateTimeOffset now) =>
        Build(request, EventTypes.ServiceRequestStatusChanged, correlationId, now,
            new ServiceRequestStatusChangedData(request.Id, request.ClientId, request.TechnicianId, previousStatus, request.Status, now));

    public static OutboxMessage Cancelled(DomainServiceRequest request, Guid correlationId, DateTimeOffset now) =>
        Build(request, EventTypes.ServiceRequestCancelled, correlationId, now,
            new ServiceRequestCancelledData(
                request.Id, request.ClientId, request.TechnicianId, request.CancelledByType!, request.CancellationReason!, now));

    public static OutboxMessage Quoted(DomainServiceRequest request, Quote quote, int quoteNumber, Guid correlationId, DateTimeOffset now) =>
        Build(request, EventTypes.Quoted, correlationId, now,
            new QuotedData(request.Id, quote.Id, request.ClientId, quote.TechnicianId, quoteNumber,
                Quote.FormatMoney(quote.TotalAmount), quote.ExpiresAt));

    public static OutboxMessage QuoteAccepted(DomainServiceRequest request, Quote quote, Guid correlationId, DateTimeOffset now) =>
        Build(request, EventTypes.QuoteAccepted, correlationId, now,
            new QuoteAcceptedData(request.Id, quote.Id, quote.TechnicianId, Quote.FormatMoney(quote.TotalAmount)));

    public static OutboxMessage QuoteRejected(
        DomainServiceRequest request, Quote quote, int remainingQuotes, Guid correlationId, DateTimeOffset now) =>
        Build(request, EventTypes.QuoteRejected, correlationId, now,
            new QuoteRejectedData(request.Id, quote.Id, quote.TechnicianId, remainingQuotes));

    public static OutboxMessage Started(DomainServiceRequest request, Guid correlationId, DateTimeOffset now) =>
        Build(request, EventTypes.ServiceStarted, correlationId, now,
            new ServiceStartedData(request.Id, request.ClientId, request.TechnicianId!.Value, request.StartedAt!.Value));

    public static OutboxMessage Completed(DomainServiceRequest request, Quote acceptedQuote, Guid correlationId, DateTimeOffset now) =>
        Build(request, EventTypes.ServiceCompleted, correlationId, now,
            new ServiceCompletedData(
                request.Id, request.ClientId, request.CompanyId, request.TechnicianId!.Value, acceptedQuote.Id,
                Quote.FormatMoney(acceptedQuote.TotalAmount), request.CompletedAt!.Value));

    private static OutboxMessage Build<TData>(
        DomainServiceRequest request, string eventType, Guid correlationId, DateTimeOffset now, TData data)
    {
        var eventId = Guid.CreateVersion7(now);
        var envelope = new EventEnvelope<TData>(
            eventId, eventType, 1, now, correlationId, request.TenantId, EventTypes.Producer, data);
        return new OutboxMessage(eventId, request.TenantId, request.Id, eventType, EventSerializer.Serialize(envelope));
    }
}