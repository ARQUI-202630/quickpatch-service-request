using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>
/// Datos para crear una solicitud. <see cref="TenantId"/> y <see cref="ClientId"/> vienen del JWT, nunca del cuerpo.
/// </summary>
public sealed record CreateServiceRequestCommand(
    Guid TenantId,
    Guid ClientId,
    Guid CorrelationId,
    Guid CategoryId,
    string? Description,
    GeoPoint? Location,
    string? AddressText);

public abstract record CreateServiceRequestResult
{
    public sealed record Created(DomainServiceRequest ServiceRequest) : CreateServiceRequestResult;

    /// <summary>RN-SR9: la ubicación está fuera del área de cobertura.</summary>
    public sealed record OutOfCoverage : CreateServiceRequestResult;

    /// <summary>RN-SR10: la categoría no existe o no está activa en el tenant.</summary>
    public sealed record CategoryUnavailable : CreateServiceRequestResult;
}

/// <summary>
/// Caso de uso de creación de solicitud (RF-07, SCRUM-27): valida, guarda la solicitud en
/// <c>buscando_tecnico</c> y registra <c>service-request.created</c> en el Outbox en la misma transacción.
/// </summary>
public sealed class CreateServiceRequestHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    ICategoryReplicaRepository categories,
    IOutbox outbox,
    CoverageArea coverageArea,
    TimeProvider clock)
{
    public async Task<CreateServiceRequestResult> HandleAsync(
        CreateServiceRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.GetUtcNow();
        var serviceRequest = DomainServiceRequest.Create(
            command.TenantId,
            command.ClientId,
            command.CategoryId,
            command.Description,
            command.Location,
            command.AddressText,
            now);

        if (!coverageArea.Contains(serviceRequest.Location))
        {
            return new CreateServiceRequestResult.OutOfCoverage();
        }

        return await unitOfWork.ExecuteAsync<CreateServiceRequestResult>(
            command.TenantId,
            async ct =>
            {
                if (!await categories.IsActiveAsync(command.CategoryId, ct))
                {
                    return new CreateServiceRequestResult.CategoryUnavailable();
                }

                serviceRequests.Add(serviceRequest);
                outbox.Enqueue(BuildCreatedEvent(serviceRequest, command.CorrelationId, now));
                return new CreateServiceRequestResult.Created(serviceRequest);
            },
            cancellationToken);
    }

    private static OutboxMessage BuildCreatedEvent(DomainServiceRequest serviceRequest, Guid correlationId, DateTimeOffset now)
    {
        var eventId = Guid.CreateVersion7(now);
        var envelope = new EventEnvelope<ServiceRequestCreatedData>(
            eventId,
            EventTypes.ServiceRequestCreated,
            1,
            now,
            correlationId,
            serviceRequest.TenantId,
            EventTypes.Producer,
            new ServiceRequestCreatedData(
                serviceRequest.Id,
                serviceRequest.ClientId,
                serviceRequest.CategoryId,
                serviceRequest.Description,
                new EventLocation(serviceRequest.Location.Latitude, serviceRequest.Location.Longitude),
                serviceRequest.CreatedAt));

        return new OutboxMessage(eventId, serviceRequest.TenantId, serviceRequest.Id, EventTypes.ServiceRequestCreated, EventSerializer.Serialize(envelope));
    }
}