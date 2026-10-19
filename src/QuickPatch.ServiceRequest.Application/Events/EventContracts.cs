using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickPatch.ServiceRequest.Application.Events;

/// <summary>
/// Sobre común de los eventos (DD, sección 8.2.1; <c>quickpatch-kafka/events</c>).
/// </summary>
public sealed record EventEnvelope<TData>(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    Guid TenantId,
    string Producer,
    TData Data);

/// <summary><c>data</c> de <c>service-request.created</c> v1.</summary>
public sealed record ServiceRequestCreatedData(
    Guid ServiceRequestId,
    Guid ClientId,
    Guid CategoryId,
    string Description,
    EventLocation Location,
    DateTimeOffset CreatedAt);

public sealed record EventLocation(double Latitude, double Longitude);

/// <summary>
/// <c>data</c> de <c>service-request.status-changed</c> v1 (SCRUM-466): el cambio de estado que ServiceRequest aplicó.
/// <see cref="TechnicianId"/> es nulo si la solicitud no tiene técnico.
/// </summary>
public sealed record ServiceRequestStatusChangedData(
    Guid ServiceRequestId,
    Guid ClientId,
    Guid? TechnicianId,
    string PreviousStatus,
    string Status,
    DateTimeOffset ChangedAt);

/// <summary>
/// <c>data</c> de <c>service-request.cancelled</c> v1 (RF-36, SCRUM-474). <see cref="TechnicianId"/> es nulo si la solicitud
/// no tenía técnico. <see cref="Reason"/> es un dato del usuario: no se escribe en los logs.
/// </summary>
public sealed record ServiceRequestCancelledData(
    Guid ServiceRequestId,
    Guid ClientId,
    Guid? TechnicianId,
    string CancelledByType,
    string Reason,
    DateTimeOffset CancelledAt);

/// <summary><c>data</c> de <c>catalog.category-changed</c> v1.</summary>
public sealed record CategoryChangedData(Guid CategoryId, string Name, bool Active, DateTimeOffset UpdatedAt);

/// <summary><c>data</c> de <c>matching.technician-assigned</c> v1.</summary>
public sealed record TechnicianAssignedData(Guid ServiceRequestId, Guid ClientId, Guid AttemptId, Guid TechnicianId, DateTimeOffset AssignedAt);

/// <summary><c>data</c> de <c>matching.no-technician-available</c> v1.</summary>
public sealed record NoTechnicianAvailableData(Guid ServiceRequestId, Guid ClientId, int AttemptsCount);

/// <summary><c>data</c> de <c>matching.search-resumed</c> v1.</summary>
public sealed record SearchResumedData(Guid ServiceRequestId, Guid ClientId, string Trigger);

public static class EventTypes
{
    public const string ServiceRequestCreated = "service-request.created";
    public const string ServiceRequestStatusChanged = "service-request.status-changed";
    public const string ServiceRequestCancelled = "service-request.cancelled";
    public const string CategoryChanged = "catalog.category-changed";
    public const string TechnicianAssigned = "matching.technician-assigned";
    public const string NoTechnicianAvailable = "matching.no-technician-available";
    public const string SearchResumed = "matching.search-resumed";
    public const string Producer = "service-request-service";
}

public static class EventSerializer
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}