using QuickPatch.ServiceRequest.Domain.Common;

namespace QuickPatch.ServiceRequest.Domain.Ratings;

/// <summary>
/// Calificación que el dueño de la solicitud da al servicio cuando ya está pagado (DD 5.9, RF-12, ADR-027). Es un hecho de
/// la solicitud: no se edita ni se borra, y cada solicitud tiene una sola (RN-R2).
/// </summary>
public sealed class Rating
{
    public const int MinScore = 1;
    public const int MaxScore = 5;

    /// <summary>Longitud máxima del comentario (ADR-027, decisión 1).</summary>
    public const int MaxCommentLength = 500;

    private Rating(Guid id, Guid tenantId, Guid serviceRequestId, Guid technicianId, int score, string? comment, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ServiceRequestId = serviceRequestId;
        TechnicianId = technicianId;
        Score = score;
        Comment = comment;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid ServiceRequestId { get; }

    /// <summary>Técnico que atendió la solicitud; es a quien llega la reputación (ADR-027).</summary>
    public Guid TechnicianId { get; }

    public int Score { get; }

    /// <summary>Comentario opcional. Nunca se registra en logs ni viaja en el evento (ADR-027, decisión 2).</summary>
    public string? Comment { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Valida el puntaje (RN-R1) y el comentario y devuelve el comentario normalizado: sin espacios a los lados y null si
    /// queda vacío. Un dato inválido es un error del campo (400).
    /// </summary>
    public static string? Validate(int? score, string? comment)
    {
        var errors = new Dictionary<string, string[]>();
        if (score is null or < MinScore or > MaxScore)
        {
            errors["score"] = [$"La calificación debe ser un número entero entre {MinScore} y {MaxScore}."];
        }

        var trimmed = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (trimmed is { Length: > MaxCommentLength })
        {
            errors["comment"] = [$"El comentario no puede tener más de {MaxCommentLength} caracteres."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return trimmed;
    }

    /// <summary>Nueva calificación ya validada con <see cref="Validate"/>.</summary>
    public static Rating Create(
        Guid tenantId, Guid serviceRequestId, Guid technicianId, int score, string? comment, DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(createdAt), tenantId, serviceRequestId, technicianId, score, comment, createdAt);

    /// <summary>Reconstruye una calificación persistida; lo usan las pruebas (EF la materializa por su constructor).</summary>
    public static Rating Restore(
        Guid id, Guid tenantId, Guid serviceRequestId, Guid technicianId, int score, string? comment, DateTimeOffset createdAt) =>
        new(id, tenantId, serviceRequestId, technicianId, score, comment, createdAt);
}