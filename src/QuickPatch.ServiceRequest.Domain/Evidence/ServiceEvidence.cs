using QuickPatch.ServiceRequest.Domain.Common;

namespace QuickPatch.ServiceRequest.Domain.Evidence;

/// <summary>
/// Foto de evidencia que el técnico asignado adjunta al servicio (DD 5.10, RF-15). El archivo vive en Garage (RN-E3); la
/// fila guarda solo la clave del objeto en <see cref="FileUrl"/>, que nunca sale en las respuestas.
/// </summary>
public sealed class ServiceEvidence
{
    /// <summary>Como máximo 10 fotos por solicitud (RN-E4).</summary>
    public const int MaxPerServiceRequest = 10;

    /// <summary>Tamaño máximo de una foto: 10 MB (DD 8.3.10).</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    private ServiceEvidence(
        Guid id, Guid tenantId, Guid serviceRequestId, Guid technicianId, string fileUrl, DateTimeOffset uploadedAt)
    {
        Id = id;
        TenantId = tenantId;
        ServiceRequestId = serviceRequestId;
        TechnicianId = technicianId;
        FileUrl = fileUrl;
        UploadedAt = uploadedAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid ServiceRequestId { get; }

    /// <summary>Técnico que subió la foto (RN-E2).</summary>
    public Guid TechnicianId { get; }

    /// <summary>Clave del objeto en el bucket de evidencias (RN-E3).</summary>
    public string FileUrl { get; }

    public DateTimeOffset UploadedAt { get; }

    /// <summary>
    /// Nueva evidencia con una clave que no depende de nada que mande el cliente: <c>{tenant}/{solicitud}/{evidencia}</c>.
    /// </summary>
    public static ServiceEvidence Create(Guid tenantId, Guid serviceRequestId, Guid technicianId, DateTimeOffset uploadedAt)
    {
        var id = Guid.CreateVersion7(uploadedAt);
        return new ServiceEvidence(id, tenantId, serviceRequestId, technicianId, $"{tenantId}/{serviceRequestId}/{id}", uploadedAt);
    }

    /// <summary>Reconstruye una evidencia persistida; lo usan las pruebas (EF la materializa por su constructor).</summary>
    public static ServiceEvidence Restore(
        Guid id, Guid tenantId, Guid serviceRequestId, Guid technicianId, string fileUrl, DateTimeOffset uploadedAt) =>
        new(id, tenantId, serviceRequestId, technicianId, fileUrl, uploadedAt);
}

/// <summary>
/// Tipos de imagen admitidos (DD 8.3.10). El tipo se reconoce por los primeros bytes del archivo, no por el nombre ni por
/// el <c>Content-Type</c> que declare el cliente.
/// </summary>
public static class EvidenceImage
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    /// <summary>Bytes que hacen falta para reconocer el tipo.</summary>
    public const int HeaderLength = 12;

    /// <summary>Devuelve el tipo de la imagen o null si no es JPEG, PNG ni WebP.</summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return Jpeg;
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        // RIFF....WEBP
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        return null;
    }

    /// <summary>
    /// Valida el archivo y devuelve su tipo. Un archivo vacío, más grande de 10 MB o de otro tipo es un error del campo
    /// <c>file</c> (400).
    /// </summary>
    public static string Validate(ReadOnlySpan<byte> content)
    {
        string? error = null;
        string? type = null;
        if (content.IsEmpty)
        {
            error = "Adjunta una foto.";
        }
        else if (content.Length > ServiceEvidence.MaxBytes)
        {
            error = "La foto no puede pesar más de 10 MB.";
        }
        else
        {
            type = Detect(content[..Math.Min(content.Length, HeaderLength)]);
            error = type is null ? "La foto debe ser JPEG, PNG o WebP." : null;
        }

        if (error is not null)
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["file"] = [error] });
        }

        return type!;
    }
}