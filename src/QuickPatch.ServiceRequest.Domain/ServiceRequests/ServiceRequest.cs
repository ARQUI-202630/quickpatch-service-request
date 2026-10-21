using QuickPatch.ServiceRequest.Domain.Common;

namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>Solicitud de servicio (DD, tabla <c>service_requests</c>).</summary>
public sealed class ServiceRequest
{
    public const int DescriptionMinLength = 10;
    public const int DescriptionMaxLength = 1000;
    public const int AddressMinLength = 5;
    public const int AddressMaxLength = 255;
    public const int CancellationReasonMinLength = 5;
    public const int CancellationReasonMaxLength = 500;
    public const int SiteNameMinLength = 2;
    public const int SiteNameMaxLength = 100;

    /// <summary>Estados anteriores a <c>en_progreso</c>, desde los que se puede cancelar (RN-SR7).</summary>
    public static readonly IReadOnlySet<string> CancellableStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        ServiceRequestStatus.BuscandoTecnico,
        ServiceRequestStatus.EnEspera,
        ServiceRequestStatus.Asignado,
        ServiceRequestStatus.Cotizado,
        ServiceRequestStatus.CotizacionAceptada,
    };

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
        ClientType = ClientTypes.Hogar;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid ClientId { get; }

    public Guid CategoryId { get; }

    public Guid? TechnicianId { get; private set; }

    /// <summary><c>hogar</c> o <c>empresa</c>, según el rol de quien la creó (RN-SR12, contrato 1.9.0).</summary>
    public string ClientType { get; private set; }

    /// <summary>Empresa cliente de la solicitud (claim <c>company_id</c>); null en las de hogar.</summary>
    public Guid? CompanyId { get; private set; }

    /// <summary>Nombre de la sede de la empresa (contrato 1.9.0); null si no se indicó.</summary>
    public string? SiteName { get; private set; }

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
    /// La cobertura (RN-SR9) y la categoría (RN-SR10) las valida el caso de uso. Con <paramref name="companyId"/> es de
    /// empresa (RF-08, RN-SR12) y admite <paramref name="siteName"/>; sin empresa, la sede no se admite.
    /// </summary>
    public static ServiceRequest Create(
        Guid tenantId,
        Guid clientId,
        Guid categoryId,
        string? description,
        GeoPoint? location,
        string? addressText,
        DateTimeOffset createdAt,
        Guid? companyId = null,
        string? siteName = null)
    {
        var errors = CollectFieldErrors(tenantId, clientId, categoryId, description, addressText, companyId, siteName);
        if (location is null || !location.IsValid)
        {
            errors["location"] = ["La ubicación debe tener una latitud entre -90 y 90 y una longitud entre -180 y 180."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        var sede = string.IsNullOrWhiteSpace(siteName) ? null : siteName.Trim();
        return new ServiceRequest(
            Guid.CreateVersion7(createdAt), tenantId, clientId, categoryId, description!.Trim(), location!, addressText!.Trim(), createdAt)
        {
            ClientType = companyId is null ? ClientTypes.Hogar : ClientTypes.Empresa,
            CompanyId = companyId,
            SiteName = sede,
        };
    }

    /// <summary>
    /// Valida todo menos la ubicación. Sirve para no geocodificar una solicitud que de todas formas se rechazaría
    /// (contrato 1.3.0: sin <c>location</c>, ServiceRequest geocodifica <c>addressText</c>).
    /// </summary>
    public static void ValidateFieldsWithoutLocation(
        Guid tenantId, Guid clientId, Guid categoryId, string? description, string? addressText, Guid? companyId = null, string? siteName = null)
    {
        var errors = CollectFieldErrors(tenantId, clientId, categoryId, description, addressText, companyId, siteName);
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

    /// <summary>Valida la dirección escrita sola (vista previa de la ubicación, contrato 1.3.0).</summary>
    public static string ValidateAddress(string? addressText)
    {
        var direccion = addressText?.Trim() ?? string.Empty;
        if (direccion.Length is < AddressMinLength or > AddressMaxLength)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                ["addressText"] = [$"La dirección debe tener entre {AddressMinLength} y {AddressMaxLength} caracteres."],
            });
        }

        return direccion;
    }

    private static Dictionary<string, string[]> CollectFieldErrors(
        Guid tenantId, Guid clientId, Guid categoryId, string? description, string? addressText, Guid? companyId, string? siteName)
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

        if (companyId == Guid.Empty)
        {
            errors["companyId"] = ["La empresa del token no es válida."];
        }

        // Una sede en blanco cuenta como sin sede.
        if (!string.IsNullOrWhiteSpace(siteName))
        {
            var sede = siteName.Trim();
            if (companyId is null)
            {
                errors["siteName"] = ["La sede solo aplica a las solicitudes de empresa."];
            }
            else if (sede.Length is < SiteNameMinLength or > SiteNameMaxLength)
            {
                errors["siteName"] = [$"La sede debe tener entre {SiteNameMinLength} y {SiteNameMaxLength} caracteres."];
            }
        }

        return errors;
    }

    /// <summary>
    /// Si la ve <paramref name="userId"/> como dueña: la creó, o es de su empresa (RN-SR14). <paramref name="companyId"/> sale
    /// del claim <c>company_id</c> del token.
    /// </summary>
    public bool IsVisibleToRequester(Guid userId, Guid? companyId) =>
        ClientId == userId || (CompanyId is { } empresa && companyId == empresa);

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

    /// <summary>Momento de la cancelación (RF-36); null si no se canceló.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Motivo de la cancelación (RF-36). Dato del usuario: no se escribe en los logs.</summary>
    public string? CancellationReason { get; private set; }

    /// <summary>Usuario que canceló; null si canceló el propio servicio (RN-Q6, RN-Q7).</summary>
    public Guid? CancelledBy { get; private set; }

    /// <summary><c>cliente</c>, <c>admin_tenant</c> o <c>sistema</c> (RN-SR8, <see cref="CancellationActor"/>).</summary>
    public string? CancelledByType { get; private set; }

    /// <summary>Valida el motivo de la cancelación (RF-36) y lo devuelve sin espacios al inicio ni al final.</summary>
    public static string ValidateCancellationReason(string? reason)
    {
        var motivo = reason?.Trim() ?? string.Empty;
        if (motivo.Length is < CancellationReasonMinLength or > CancellationReasonMaxLength)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = [$"El motivo debe tener entre {CancellationReasonMinLength} y {CancellationReasonMaxLength} caracteres."],
            });
        }

        return motivo;
    }

    /// <summary>
    /// Cancela la solicitud si el servicio no ha iniciado (RN-SR7) y registra quién y por qué (RN-SR8). Devuelve false,
    /// sin cambiar nada, desde <c>en_progreso</c>, <c>completado</c>, <c>pagado</c> o <c>cancelado</c>.
    /// </summary>
    public bool TryCancel(Guid? cancelledBy, string cancelledByType, string reason, DateTimeOffset cancelledAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelledByType);
        if (!CancellationActor.All.Contains(cancelledByType))
        {
            throw new ArgumentException("Tipo de cancelación desconocido.", nameof(cancelledByType));
        }

        if (!CancellableStatuses.Contains(Status))
        {
            return false;
        }

        Status = ServiceRequestStatus.Cancelado;
        CancelledAt = cancelledAt;
        CancellationReason = ValidateCancellationReason(reason);
        CancelledBy = cancelledBy;
        CancelledByType = cancelledByType;
        return true;
    }

    /// <summary>El técnico asignado emitió una cotización: <c>asignado</c> pasa a <c>cotizado</c> (RN-Q1). False si no está en <c>asignado</c>.</summary>
    public bool TryMarkQuoted() => TryMove(ServiceRequestStatus.Asignado, ServiceRequestStatus.Cotizado);

    /// <summary>El cliente aceptó la cotización: <c>cotizado</c> pasa a <c>cotizacion_aceptada</c> y el servicio puede iniciar (RN-SR6).</summary>
    public bool TryAcceptQuote() => TryMove(ServiceRequestStatus.Cotizado, ServiceRequestStatus.CotizacionAceptada);

    /// <summary>
    /// El cliente rechazó una cotización y le quedan más: <c>cotizado</c> vuelve a <c>asignado</c> para que el técnico emita
    /// otra (DD 7.4). Con la tercera, la solicitud se cancela con <see cref="TryCancel"/> (RN-Q7).
    /// </summary>
    public bool TryReturnToAssigned() => TryMove(ServiceRequestStatus.Cotizado, ServiceRequestStatus.Asignado);

    /// <summary>
    /// Payments confirmó el pago (<c>payment.approved</c>, RN-SR4): <c>completado</c> pasa a <c>pagado</c>. False, sin cambiar
    /// nada, desde otro estado; un rechazo no llama aquí y la solicitud sigue en <c>completado</c> (RN-SR5).
    /// </summary>
    public bool TryMarkPaid() => TryMove(ServiceRequestStatus.Completado, ServiceRequestStatus.Pagado);

    /// <summary>Inicio del servicio (RF-14); null antes de <c>en_progreso</c>.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Fin del servicio (RF-15); null antes de <c>completado</c>.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// El técnico asignado inició el servicio: <c>cotizacion_aceptada</c> pasa a <c>en_progreso</c> (RN-SR6). False, sin
    /// cambiar nada, desde otro estado.
    /// </summary>
    public bool TryStart(DateTimeOffset startedAt)
    {
        if (!TryMove(ServiceRequestStatus.CotizacionAceptada, ServiceRequestStatus.EnProgreso))
        {
            return false;
        }

        StartedAt = startedAt;
        return true;
    }

    /// <summary>
    /// El técnico asignado completó el servicio: <c>en_progreso</c> pasa a <c>completado</c>. La evidencia (RN-SR3, RN-E1) la
    /// cuenta el caso de uso en la misma transacción, antes de llamar aquí. False, sin cambiar nada, desde otro estado.
    /// </summary>
    public bool TryComplete(DateTimeOffset completedAt)
    {
        if (!TryMove(ServiceRequestStatus.EnProgreso, ServiceRequestStatus.Completado))
        {
            return false;
        }

        CompletedAt = completedAt;
        return true;
    }

    private bool TryMove(string from, string to)
    {
        if (Status != from)
        {
            return false;
        }

        Status = to;
        return true;
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
        DateTimeOffset? assignedAt = null,
        Guid? companyId = null,
        string? siteName = null) =>
        new(id, tenantId, clientId, categoryId, description, location, addressText, createdAt)
        {
            TechnicianId = technicianId,
            Status = status,
            AssignedAt = assignedAt,
            ClientType = companyId is null ? ClientTypes.Hogar : ClientTypes.Empresa,
            CompanyId = companyId,
            SiteName = siteName,
        };
}

/// <summary>Tipo de cliente de la solicitud (<c>client_type</c>, RN-SR12).</summary>
public static class ClientTypes
{
    public const string Hogar = "hogar";
    public const string Empresa = "empresa";
}

/// <summary>Quién canceló la solicitud (<c>cancelled_by_type</c>, RN-SR8).</summary>
public static class CancellationActor
{
    public const string Cliente = "cliente";
    public const string AdminTenant = "admin_tenant";
    public const string Sistema = "sistema";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Cliente, AdminTenant, Sistema };
}