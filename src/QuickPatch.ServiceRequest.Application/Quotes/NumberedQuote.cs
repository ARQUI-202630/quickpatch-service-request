using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Quotes;

/// <summary>Cotización con su posición en la solicitud (1 a 3, RN-Q7), como la devuelve el contrato 1.6.0.</summary>
public sealed record NumberedQuote(Quote Quote, int QuoteNumber);