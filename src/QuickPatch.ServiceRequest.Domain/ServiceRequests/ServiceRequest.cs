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

    public string Description { get; }

    public GeoPoint Location { get; }

    public string AddressText { get; }

    public string Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

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
        DateTimeOffset createdAt) =>
        new(id, tenantId, clientId, categoryId, description, location, addressText, createdAt)
        {
            TechnicianId = technicianId,
            Status = status,
        };
}