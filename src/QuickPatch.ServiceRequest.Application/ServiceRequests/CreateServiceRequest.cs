using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>
/// Datos para crear una solicitud. <see cref="TenantId"/>, <see cref="ClientId"/> y <see cref="CompanyId"/> vienen del JWT,
/// nunca del cuerpo (RN-SR12): con empresa, la solicitud es de empresa y admite <see cref="SiteName"/> (contrato 1.9.0).
/// </summary>
public sealed record CreateServiceRequestCommand(
    Guid TenantId,
    Guid ClientId,
    Guid CorrelationId,
    Guid CategoryId,
    string? Description,
    GeoPoint? Location,
    string? AddressText,
    Guid? CompanyId = null,
    string? SiteName = null);

public abstract record CreateServiceRequestResult
{
    public sealed record Created(DomainServiceRequest ServiceRequest) : CreateServiceRequestResult;

    /// <summary>RN-SR9: la ubicación está fuera del área de cobertura o de las 20 localidades.</summary>
    public sealed record OutOfCoverage : CreateServiceRequestResult;

    /// <summary>RN-SR10: la categoría no existe o no está activa en el tenant.</summary>
    public sealed record CategoryUnavailable : CreateServiceRequestResult;

    /// <summary>Llegó sin <c>location</c> y la dirección no se pudo geocodificar (contrato 1.3.0, ADR-024).</summary>
    public sealed record AddressNotFound : CreateServiceRequestResult;
}

/// <summary>
/// Caso de uso de creación de solicitud (RF-07, SCRUM-27): valida, guarda la solicitud en
/// <c>buscando_tecnico</c> y registra <c>service-request.created</c> en el Outbox en la misma transacción.
/// Sin <c>location</c>, geocodifica <c>addressText</c> antes de abrir la transacción (contrato 1.3.0, DD 8.3.3).
/// La cobertura (RN-SR9) es la misma de la vista previa: el rectángulo y una de las 20 localidades (SCRUM-636). El
/// rectángulo solo no basta: incluye parte de Soacha, y una dirección de otra ciudad puede caer allí.
/// </summary>
public sealed class CreateServiceRequestHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    ICategoryReplicaRepository categories,
    IOutbox outbox,
    IGeocoder geocoder,
    ILocalityCatalog localities,
    CoverageArea coverageArea,
    TimeProvider clock)
{
    public async Task<CreateServiceRequestResult> HandleAsync(
        CreateServiceRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var location = command.Location;
        if (location is null)
        {
            // Primero el resto de los campos: no se geocodifica una solicitud que de todas formas se rechazaría.
            DomainServiceRequest.ValidateFieldsWithoutLocation(
                command.TenantId, command.ClientId, command.CategoryId, command.Description, command.AddressText, command.CompanyId, command.SiteName);
            var geocoded = await geocoder.GeocodeAsync(command.AddressText!.Trim(), cancellationToken);
            if (geocoded is null)
            {
                return new CreateServiceRequestResult.AddressNotFound();
            }

            location = geocoded.Location;
        }

        var now = clock.GetUtcNow();
        var serviceRequest = DomainServiceRequest.Create(
            command.TenantId,
            command.ClientId,
            command.CategoryId,
            command.Description,
            location,
            command.AddressText,
            now,
            command.CompanyId,
            command.SiteName);

        if (!coverageArea.Contains(serviceRequest.Location)
            || await localities.FindContainingAsync(serviceRequest.Location, cancellationToken) is null)
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