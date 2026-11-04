using System.Net;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Infrastructure.Options;

namespace QuickPatch.ServiceRequest.Infrastructure.Storage;

/// <summary>
/// Evidencias en Garage por su API S3 (ADR-016, RN-E3): un objeto por foto en el bucket <c>service-request-evidencias</c>,
/// con la llave <c>service-request</c>. Las claves vienen del caso de uso y nunca se exponen.
/// </summary>
public sealed class GarageEvidenceStorage(IAmazonS3 s3, EvidenceStorageOptions options) : IEvidenceStorage
{
    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(content.ToArray(), writable: false);
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = key,
                InputStream = body,
                ContentType = contentType,
                AutoCloseStream = false,
            },
            cancellationToken);
    }

    public async Task<StoredEvidence?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await s3.GetObjectAsync(options.Bucket, key, cancellationToken);
            return new StoredEvidence(response.ResponseStream, response.Headers.ContentType, response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await s3.DeleteObjectAsync(options.Bucket, key, cancellationToken);

    /// <summary>Cliente S3 para Garage: estilo de ruta (sin subdominio por bucket) y la región que declara Garage.</summary>
    public static AmazonS3Client CreateClient(EvidenceStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = options.Region,
                // Garage no exige sumas de verificación adicionales; se envían solo cuando la operación las requiere.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                Timeout = options.Timeout,
                MaxErrorRetry = 2,
            });
    }
}

/// <summary>
/// Sin Garage configurado (desarrollo local): falla cerrado. Subir o descargar una evidencia responde 500 en lugar de
/// guardar la foto en un lugar que no es el aprobado.
/// </summary>
public sealed class UnconfiguredEvidenceStorage : IEvidenceStorage
{
    public Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task<StoredEvidence?> GetAsync(string key, CancellationToken cancellationToken) => throw NotConfigured();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw NotConfigured();

    private static InvalidOperationException NotConfigured() =>
        new("El almacenamiento de evidencias no está configurado (Evidence__Endpoint).");
}