using QuickPatch.ServiceRequest.Domain.Common;

namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>
/// Reglas de validación de los datos de una solicitud (RN-SR1, RN-SR11, RN-SR12 y RF-36). Están fuera del agregado
/// <see cref="ServiceRequest"/> porque también se usan antes de que exista una solicitud: la vista previa de la
/// ubicación y la validación previa a geocodificar (contrato 1.3.0). El agregado las usa al crearse y al cancelarse.
/// </summary>
public static class ServiceRequestRules
{
    public const int DescriptionMinLength = 10;
    public const int DescriptionMaxLength = 1000;
    public const int AddressMinLength = 5;
    public const int AddressMaxLength = 255;
    public const int CancellationReasonMinLength = 5;
    public const int CancellationReasonMaxLength = 500;
    public const int SiteNameMinLength = 2;
    public const int SiteNameMaxLength = 100;

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

    internal static Dictionary<string, string[]> CollectFieldErrors(
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
}