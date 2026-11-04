using System.Globalization;
using System.Text.RegularExpressions;

using QuickPatch.ServiceRequest.Domain.Common;

namespace QuickPatch.ServiceRequest.Domain.Quotes;

/// <summary>
/// Cotización de mano de obra y materiales que el técnico asignado presenta antes de iniciar el servicio (DD 5.11,
/// RF-34, RF-35). El total de la cotización aceptada es el monto del pago.
/// </summary>
public sealed partial class Quote
{
    /// <summary>Una solicitud admite como máximo 3 cotizaciones (RN-Q7).</summary>
    public const int MaxPerServiceRequest = 3;

    public const int DetailMaxLength = 1000;
    public const int RejectionReasonMinLength = 5;
    public const int RejectionReasonMaxLength = 500;

    /// <summary>Motivo de la cancelación cuando el cliente no responde a tiempo (RN-Q6).</summary>
    public const string ExpiredCancellationReason = "Cotización sin respuesta.";

    /// <summary>Motivo de la cancelación cuando el cliente rechaza la tercera cotización (RN-Q7).</summary>
    public const string RejectedCancellationReason = "Cotizaciones rechazadas.";

    private Quote(
        Guid id,
        Guid tenantId,
        Guid serviceRequestId,
        Guid technicianId,
        decimal laborAmount,
        decimal materialsAmount,
        string? detail,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        TenantId = tenantId;
        ServiceRequestId = serviceRequestId;
        TechnicianId = technicianId;
        LaborAmount = laborAmount;
        MaterialsAmount = materialsAmount;
        TotalAmount = laborAmount + materialsAmount;
        Detail = detail;
        Status = QuoteStatus.Pendiente;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid ServiceRequestId { get; }

    public Guid TechnicianId { get; }

    public decimal LaborAmount { get; }

    public decimal MaterialsAmount { get; }

    /// <summary><c>labor_amount + materials_amount</c>; en la base es una columna generada.</summary>
    public decimal TotalAmount { get; private set; }

    public string? Detail { get; }

    public string Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Límite para que el cliente responda (RN-Q8).</summary>
    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RespondedAt { get; private set; }

    /// <summary>Motivo opcional del rechazo (DD 5.11, v5.5). Dato del usuario: no se escribe en los logs.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>
    /// Emite una cotización <c>pendiente</c> que vence <paramref name="ttl"/> después (RN-Q8). Los montos llegan como texto
    /// con dos decimales (esquema <c>Money</c> del contrato 1.6.0); la mano de obra debe ser mayor que 0.
    /// </summary>
    public static Quote Issue(
        Guid tenantId,
        Guid serviceRequestId,
        Guid technicianId,
        string? laborAmount,
        string? materialsAmount,
        string? detail,
        DateTimeOffset createdAt,
        TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "El plazo de la cotización debe ser positivo.");
        }

        var errors = new Dictionary<string, string[]>();
        var labor = ParseMoney(laborAmount, "laborAmount", errors);
        var materials = ParseMoney(materialsAmount, "materialsAmount", errors);
        if (labor is <= 0m)
        {
            errors["laborAmount"] = ["La mano de obra debe ser mayor que 0."];
        }

        var detalle = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        if (detalle is { Length: > DetailMaxLength })
        {
            errors["detail"] = [$"El detalle debe tener como máximo {DetailMaxLength} caracteres."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return new Quote(
            Guid.CreateVersion7(createdAt), tenantId, serviceRequestId, technicianId, labor!.Value, materials!.Value, detalle,
            createdAt, createdAt + ttl);
    }

    /// <summary>Valida el motivo opcional del rechazo: null o vacío es "sin motivo"; si llega, de 5 a 500 caracteres.</summary>
    public static string? ValidateRejectionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var motivo = reason.Trim();
        if (motivo.Length is < RejectionReasonMinLength or > RejectionReasonMaxLength)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = [$"El motivo debe tener entre {RejectionReasonMinLength} y {RejectionReasonMaxLength} caracteres."],
            });
        }

        return motivo;
    }

    /// <summary>Pendiente y todavía dentro del plazo: el cliente puede aceptarla o rechazarla.</summary>
    public bool IsOpenAt(DateTimeOffset now) => Status == QuoteStatus.Pendiente && now < ExpiresAt;

    /// <summary>El cliente la acepta (RN-Q3, RN-Q4). Devuelve false, sin cambios, si no está abierta.</summary>
    public bool TryAccept(DateTimeOffset now)
    {
        if (!IsOpenAt(now))
        {
            return false;
        }

        Status = QuoteStatus.Aceptada;
        RespondedAt = now;
        return true;
    }

    /// <summary>El cliente la rechaza con un motivo opcional ya validado. Devuelve false, sin cambios, si no está abierta.</summary>
    public bool TryReject(string? reason, DateTimeOffset now)
    {
        if (!IsOpenAt(now))
        {
            return false;
        }

        Status = QuoteStatus.Rechazada;
        RespondedAt = now;
        RejectionReason = reason;
        return true;
    }

    /// <summary>La solicitud se canceló con la cotización pendiente (RN-Q5).</summary>
    public bool TryAnnul()
    {
        if (Status != QuoteStatus.Pendiente)
        {
            return false;
        }

        Status = QuoteStatus.Anulada;
        return true;
    }

    /// <summary>El cliente no respondió antes de <see cref="ExpiresAt"/> (RN-Q6). Devuelve false si no está pendiente o no ha vencido.</summary>
    public bool TryExpire(DateTimeOffset now)
    {
        if (Status != QuoteStatus.Pendiente || now < ExpiresAt)
        {
            return false;
        }

        Status = QuoteStatus.Expirada;
        return true;
    }

    /// <summary>Monto con dos decimales, como lo publica el contrato (<c>Money</c>).</summary>
    public static string FormatMoney(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static decimal? ParseMoney(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null || !MoneyPattern().IsMatch(value))
        {
            errors[field] = ["Debe ser un monto en pesos con dos decimales, por ejemplo 120000.00."];
            return null;
        }

        return decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\d{1,10}\.\d{2}$")]
    private static partial Regex MoneyPattern();

    /// <summary>Reconstruye una cotización persistida; lo usan las pruebas (EF la materializa por su constructor).</summary>
    public static Quote Restore(
        Guid id,
        Guid tenantId,
        Guid serviceRequestId,
        Guid technicianId,
        decimal laborAmount,
        decimal materialsAmount,
        string? detail,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? respondedAt = null,
        string? rejectionReason = null) =>
        new(id, tenantId, serviceRequestId, technicianId, laborAmount, materialsAmount, detail, createdAt, expiresAt)
        {
            Status = status,
            RespondedAt = respondedAt,
            RejectionReason = rejectionReason,
        };
}

/// <summary>Estados de la cotización (DD 5.11). Se guardan como texto en <c>quotes.status</c>.</summary>
public static class QuoteStatus
{
    public const string Pendiente = "pendiente";
    public const string Aceptada = "aceptada";
    public const string Rechazada = "rechazada";
    public const string Anulada = "anulada";
    public const string Expirada = "expirada";
}