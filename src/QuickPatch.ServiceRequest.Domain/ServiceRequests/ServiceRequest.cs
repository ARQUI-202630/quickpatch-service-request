using QuickPatch.ServiceRequest.Domain.Common;

namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>Solicitud de servicio (DD, tabla <c>service_requests</c>).</summary>
public sealed class ServiceRequest
{
    public const int DescriptionMinLength = 10;
    public const int DescriptionMaxLength = 1000;
    public const int AddressMinLength = 5;
    public const int AddressMaxLength = 255;

    private ServiceRequest(
        Guid id,
        Guid tenantId,
        Guid clientId,
        Guid categoryId,
        string description,
        GeoPoint location,
        string addressText,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ClientId = clientId;
        CategoryId = categoryId;
        Description = description;
        Location = location;
        AddressText = addressText;
        Status = ServiceRequestStatus.BuscandoTecnico;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid ClientId { get; }

    public Guid CategoryId { get; }

    public Guid? TechnicianId { get; private set; }

    /// <summary>Momento en que el técnico aceptó la oferta (<c>matching.technician-assigned</c>); null antes.</summary>
    public DateTimeOffset? AssignedAt { get; private set; }

    public string Description { get; }

    public GeoPoint Location { get; }

    public string AddressText { get; }

    public string Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// <c>occurredAt</c> del último evento de Matching aplicado. Los eventos de Matching llegan por topics distintos
    /// y pueden llegar en otro orden; uno más viejo que este se descarta (ADR-017).
    /// </summary>
    public DateTimeOffset? MatchingUpdatedAt { get; private set; }

    /// <summary>
    /// Crea una solicitud en estado <c>buscando_tecnico</c> (RF-07), validando RN-SR1 y RN-SR11.
    /// La cobertura (RN-SR9) y la categoría (RN-SR10) las valida el caso de uso.
    /// </summary>
    public static ServiceRequest Create(
        Guid tenantId,
        Guid clientId,
        Guid categoryId,
        string? description,
        GeoPoint? location,
        string? addressText,
        DateTimeOffset createdAt)
    {
        var errors = new Dictionary<string, string[]>();
        var descripcion = description?.Trim() ?? string.Empty;
        var direccion = addressText?.Trim() ?? string.Empty;

        if (tenantId == Guid.Empty)
        {
            errors["tenantId"] = ["El tenant es obligatorio."];
        }

        if (clientId == Guid.Empty)
        {
            errors["clientId"] = ["El cliente es obligatorio."];
        }

        if (categoryId == Guid.Empty)
        {
            errors["categoryId"] = ["La categoría es obligatoria."];
        }

        if (descripcion.Length is < DescriptionMinLength or > DescriptionMaxLength)
        {
            errors["description"] = [$"La descripción debe tener entre {DescriptionMinLength} y {DescriptionMaxLength} caracteres."];
        }

        if (direccion.Length is < AddressMinLength or > AddressMaxLength)
        {
            errors["addressText"] = [$"La dirección debe tener entre {AddressMinLength} y {AddressMaxLength} caracteres."];
        }

        if (location is null || !location.IsValid)
        {
            errors["location"] = ["La ubicación debe tener una latitud entre -90 y 90 y una longitud entre -180 y 180."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return new ServiceRequest(
            Guid.CreateVersion7(createdAt), tenantId, clientId, categoryId, descripcion, location!, direccion, createdAt);
    }

    /// <summary>
    /// <c>matching.technician-assigned</c>: <c>buscando_tecnico</c> pasa a <c>asignado</c> con el técnico y la hora de
    /// asignación (DD 7.4, SCRUM-91).
    /// </summary>
    public MatchingEventOutcome ApplyTechnicianAssigned(Guid technicianId, DateTimeOffset assignedAt, DateTimeOffset occurredAt)
    {
        var outcome = ApplyMatchingEvent(occurredAt, ServiceRequestStatus.BuscandoTecnico, ServiceRequestStatus.Asignado);
        if (outcome == MatchingEventOutcome.Applied)
        {
            TechnicianId = technicianId;
            AssignedAt = assignedAt;
        }

        return outcome;
    }

    /// <summary>
    /// Estados en los que el técnico asignado ve la solicitud con la dirección y el contacto del cliente
    /// (RN-SR13): de <c>asignado</c> a <c>completado</c>.
    /// </summary>
    public bool IsVisibleToAssignedTechnician => Status is ServiceRequestStatus.Asignado
        or ServiceRequestStatus.Cotizado
        or ServiceRequestStatus.CotizacionAceptada
        or ServiceRequestStatus.EnProgreso
        or ServiceRequestStatus.Completado;

    /// <summary>
    /// <c>matching.no-technician-available</c>: <c>buscando_tecnico</c> pasa a <c>en_espera</c> (DD 7.4, RN-M4).
    /// </summary>
    public MatchingEventOutcome ApplyNoTechnicianAvailable(DateTimeOffset occurredAt) =>
        ApplyMatchingEvent(occurredAt, ServiceRequestStatus.BuscandoTecnico, ServiceRequestStatus.EnEspera);

    /// <summary>
    /// <c>matching.search-resumed</c>: <c>en_espera</c> vuelve a <c>buscando_tecnico</c> (DD 7.4, RN-M10).
    /// </summary>
    public MatchingEventOutcome ApplySearchResumed(DateTimeOffset occurredAt) =>
        ApplyMatchingEvent(occurredAt, ServiceRequestStatus.EnEspera, ServiceRequestStatus.BuscandoTecnico);

    /// <summary>
    /// Aplica la transición si el evento es más reciente que el último de Matching y la solicitud está en el estado
    /// de origen. Un evento reciente que no corresponde al estado (por ejemplo, la solicitud ya se canceló) no cambia
    /// el estado, pero sí la versión: así un evento más viejo que llegue después tampoco lo cambia.
    /// </summary>
    private MatchingEventOutcome ApplyMatchingEvent(DateTimeOffset occurredAt, string from, string to)
    {
        if (MatchingUpdatedAt is { } last && occurredAt <= last)
        {
            return MatchingEventOutcome.Stale;
        }

        MatchingUpdatedAt = occurredAt;
        if (Status != from)
        {
            return MatchingEventOutcome.Ignored;
        }

        Status = to;
        return MatchingEventOutcome.Applied;
    }

    /// <summary>Reconstruye una solicitud persistida; lo usa el repositorio.</summary>
    public static ServiceRequest Restore(
        Guid id,
        Guid tenantId,
        Guid clientId,
        Guid categoryId,
        Guid? technicianId,
        string description,
        GeoPoint location,
        string addressText,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset? assignedAt = null) =>
        new(id, tenantId, clientId, categoryId, description, location, addressText, createdAt)
        {
            TechnicianId = technicianId,
            Status = status,
            AssignedAt = assignedAt,
        };
}