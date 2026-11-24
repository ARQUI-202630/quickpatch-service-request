using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary><c>POST .../evidence</c> (contrato 1.7.0): una foto por petición.</summary>
public sealed record UploadEvidenceCommand(Guid TenantId, Guid TechnicianId, Guid ServiceRequestId, ReadOnlyMemory<byte> Content);

public abstract record UploadEvidenceResult
{
    public sealed record Uploaded(ServiceEvidence Evidence) : UploadEvidenceResult;

    public sealed record NotFound : UploadEvidenceResult;

    /// <summary>La solicitud no está en <c>en_progreso</c>.</summary>
    public sealed record InvalidTransition(string Status) : UploadEvidenceResult;

    /// <summary>La solicitud ya tiene 10 evidencias (RN-E4).</summary>
    public sealed record LimitReached : UploadEvidenceResult;
}

/// <summary>
/// El técnico asignado sube una foto (RF-15, RN-E2). Primero valida el archivo y comprueba, sin bloquear, que la solicitud
/// sea suya, esté en <c>en_progreso</c> y tenga menos de 10 fotos, para no escribir en Garage algo que se va a rechazar.
/// Después sube el archivo y, con la fila de la solicitud bloqueada, vuelve a comprobar y guarda la fila. Si la segunda
/// comprobación falla (por ejemplo, el técnico completó en paralelo) o la transacción no se confirma, borra el objeto.
/// </summary>
public sealed partial class UploadEvidenceHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IEvidenceRepository evidence,
    IEvidenceStorage storage,
    TimeProvider clock,
    ILogger<UploadEvidenceHandler> logger)
{
    public async Task<UploadEvidenceResult> HandleAsync(UploadEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contentType = EvidenceImage.Validate(command.Content.Span);

        var precheck = await unitOfWork.ExecuteAsync(
            command.TenantId,
            async ct => Check(
                await serviceRequests.FindForTechnicianAsync(command.ServiceRequestId, command.TechnicianId, ct),
                command,
                await evidence.CountAsync(command.ServiceRequestId, ct)),
            cancellationToken);
        if (precheck is not null)
        {
            return precheck;
        }

        var created = ServiceEvidence.Create(command.TenantId, command.ServiceRequestId, command.TechnicianId, clock.GetUtcNow());
        await storage.PutAsync(created.FileUrl, command.Content, contentType, cancellationToken);

        UploadEvidenceResult result;
        try
        {
            result = await unitOfWork.ExecuteAsync(
                command.TenantId,
                async ct =>
                {
                    var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                    var rejected = Check(request, command, await evidence.CountAsync(command.ServiceRequestId, ct));
                    if (rejected is not null)
                    {
                        return rejected;
                    }

                    evidence.Add(created);
                    return new UploadEvidenceResult.Uploaded(created);
                },
                cancellationToken);
        }
        catch
        {
            await DeleteQuietlyAsync(created);
            throw;
        }

        if (result is not UploadEvidenceResult.Uploaded)
        {
            await DeleteQuietlyAsync(created);
        }

        return result;
    }

    private static UploadEvidenceResult? Check(DomainServiceRequest? request, UploadEvidenceCommand command, int count)
    {
        if (request is null || request.TechnicianId != command.TechnicianId)
        {
            return new UploadEvidenceResult.NotFound();
        }

        if (request.Status != ServiceRequestStatus.EnProgreso)
        {
            return new UploadEvidenceResult.InvalidTransition(request.Status);
        }

        return count >= ServiceEvidence.MaxPerServiceRequest ? new UploadEvidenceResult.LimitReached() : null;
    }

    private async Task DeleteQuietlyAsync(ServiceEvidence orphan)
    {
        try
        {
            // Sin el token de la petición: si el cliente se fue, el objeto se borra igual.
            await storage.DeleteAsync(orphan.FileUrl, CancellationToken.None);
        }
#pragma warning disable CA1031 // Un objeto huérfano no debe ocultar el resultado de la subida.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogOrphan(logger, ex, orphan.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo borrar el archivo de la evidencia {EvidenceId}, que no quedó guardada.")]
    private static partial void LogOrphan(ILogger logger, Exception exception, Guid evidenceId);
}