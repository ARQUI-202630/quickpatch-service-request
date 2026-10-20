using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary><c>POST .../start</c> o <c>.../complete</c> (contrato 1.7.0). El técnico y el tenant salen del JWT.</summary>
public sealed record ServiceStepCommand(Guid TenantId, Guid TechnicianId, Guid ServiceRequestId, Guid CorrelationId);

public abstract record ServiceStepResult
{
    public sealed record Done(DomainServiceRequest ServiceRequest) : ServiceStepResult;

    /// <summary>No existe, es de otro tenant (RLS) o está asignada a otro técnico: 404 para no revelarla.</summary>
    public sealed record NotFound : ServiceStepResult;

    /// <summary>La solicitud no está en el estado que exige el paso (RN-SR6 o RN-SR3).</summary>
    public sealed record InvalidTransition(string Status) : ServiceStepResult;

    /// <summary>Completar sin ninguna evidencia (RN-SR3, RN-E1).</summary>
    public sealed record EvidenceRequired : ServiceStepResult;
}

/// <summary>
/// El técnico asignado inicia el servicio (RF-14): con la fila de la solicitud bloqueada, <c>cotizacion_aceptada</c> pasa a
/// <c>en_progreso</c> (RN-SR6) y quedan en el Outbox <c>service-request.started</c> y <c>service-request.status-changed</c>.
/// </summary>
public sealed class StartServiceHandler(
    ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, IOutbox outbox, TimeProvider clock)
{
    public async Task<ServiceStepResult> HandleAsync(ServiceStepCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await unitOfWork.ExecuteAsync<ServiceStepResult>(
            command.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                if (request is null || request.TechnicianId != command.TechnicianId)
                {
                    return new ServiceStepResult.NotFound();
                }

                var now = clock.GetUtcNow();
                var previousStatus = request.Status;
                if (!request.TryStart(now))
                {
                    return new ServiceStepResult.InvalidTransition(request.Status);
                }

                outbox.Enqueue(ServiceRequestEvents.Started(request, command.CorrelationId, now));
                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, command.CorrelationId, now));
                return new ServiceStepResult.Done(request);
            },
            cancellationToken);
    }
}

/// <summary>
/// El técnico asignado completa el servicio (RF-15, DD 8.3.11). Con la fila de la solicitud bloqueada cuenta las evidencias
/// (RN-SR3: en la misma transacción, para no cruzarse con una subida), pasa <c>en_progreso</c> a <c>completado</c> y deja en
/// el Outbox <c>service-request.completed</c>, con el total de la cotización aceptada (RN-P4, RN-M6), y
/// <c>service-request.status-changed</c>.
/// </summary>
public sealed class CompleteServiceHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IEvidenceRepository evidence,
    IQuoteRepository quotes,
    IOutbox outbox,
    TimeProvider clock)
{
    public async Task<ServiceStepResult> HandleAsync(ServiceStepCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await unitOfWork.ExecuteAsync<ServiceStepResult>(
            command.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                if (request is null || request.TechnicianId != command.TechnicianId)
                {
                    return new ServiceStepResult.NotFound();
                }

                if (request.Status != ServiceRequestStatus.EnProgreso)
                {
                    return new ServiceStepResult.InvalidTransition(request.Status);
                }

                if (await evidence.CountAsync(request.Id, ct) == 0)
                {
                    return new ServiceStepResult.EvidenceRequired();
                }

                // en_progreso solo se alcanza desde cotizacion_aceptada (RN-SR6), así que hay una aceptada (RN-Q4).
                var accepted = (await quotes.ListAsync(request.Id, ct)).SingleOrDefault(q => q.Status == QuoteStatus.Aceptada)
                    ?? throw new InvalidOperationException($"La solicitud {request.Id} está en progreso sin cotización aceptada.");

                var now = clock.GetUtcNow();
                var previousStatus = request.Status;
                request.TryComplete(now);
                outbox.Enqueue(ServiceRequestEvents.Completed(request, accepted, command.CorrelationId, now));
                outbox.Enqueue(ServiceRequestEvents.StatusChanged(request, previousStatus, command.CorrelationId, now));
                return new ServiceStepResult.Done(request);
            },
            cancellationToken);
    }
}

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

/// <summary>Quién consulta las evidencias (contrato 1.7.0): el cliente dueño, el técnico asignado o el <c>admin_tenant</c>.</summary>
public enum EvidenceViewer
{
    Client,
    Technician,
    AdminTenant,
}

public sealed record EvidenceQuery(Guid TenantId, Guid UserId, EvidenceViewer Viewer, Guid ServiceRequestId);

/// <summary>
/// Evidencias de la solicitud (RF-15). Null si la solicitud no existe, es de otro tenant o no es del usuario: la API
/// responde 404. El <c>admin_tenant</c> ve las de cualquier solicitud de su tenant (RLS).
/// </summary>
public sealed class ListEvidenceHandler(
    ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, IEvidenceRepository evidence)
{
    public async Task<IReadOnlyList<ServiceEvidence>?> HandleAsync(EvidenceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await unitOfWork.ExecuteAsync<IReadOnlyList<ServiceEvidence>?>(
            query.TenantId,
            async ct =>
            {
                var request = query.Viewer switch
                {
                    EvidenceViewer.Client => await serviceRequests.FindForClientAsync(query.ServiceRequestId, query.UserId, ct),
                    EvidenceViewer.Technician => await serviceRequests.FindForTechnicianAsync(query.ServiceRequestId, query.UserId, ct),
                    _ => await serviceRequests.FindInTenantAsync(query.ServiceRequestId, ct),
                };
                return request is null ? null : await evidence.ListAsync(request.Id, ct);
            },
            cancellationToken);
    }
}

/// <summary>
/// Archivo de una evidencia (DD 8.3.10): con las mismas reglas que el listado, lo lee de Garage. Null si la solicitud o la
/// evidencia no son del usuario, o si el objeto ya no existe.
/// </summary>
public sealed class DownloadEvidenceHandler(ListEvidenceHandler list, IEvidenceStorage storage)
{
    public async Task<StoredEvidence?> HandleAsync(EvidenceQuery query, Guid evidenceId, CancellationToken cancellationToken)
    {
        var found = (await list.HandleAsync(query, cancellationToken))?.SingleOrDefault(e => e.Id == evidenceId);
        return found is null ? null : await storage.GetAsync(found.FileUrl, cancellationToken);
    }
}