using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>Validación de <c>status</c>, <c>page</c> y <c>size</c> de las listas (DD 8.1): página desde 1, hasta 100 elementos.</summary>
public static class ListFilters
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    /// <summary>Devuelve la página y el tamaño efectivos; lanza <see cref="DomainValidationException"/> con cada campo inválido.</summary>
    public static (int Page, int Size) Validate(string? status, int? page, int? size)
    {
        var effectivePage = page ?? 1;
        var effectiveSize = size ?? DefaultSize;
        var errors = new Dictionary<string, string[]>();
        if (status is not null && !ServiceRequestStatus.All.Contains(status))
        {
            errors["status"] = ["El estado no existe."];
        }

        if (effectivePage < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (effectiveSize is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return (effectivePage, effectiveSize);
    }
}

/// <summary>Inicio del día calendario de Bogotá (UTC-5, sin horario de verano), que es el «hoy» de <c>createdToday</c>.</summary>
public static class BogotaDay
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public static DateTimeOffset StartUtc(DateTimeOffset now)
    {
        var local = now.ToOffset(Offset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, Offset).ToUniversalTime();
    }
}

/// <summary>Conteo de los 9 estados (siempre todos, en el orden de DD 7.4) y de las creadas hoy.</summary>
public sealed record ServiceRequestCounts(IReadOnlyDictionary<string, int> ByStatus, int CreatedToday)
{
    /// <summary>Los estados de DD 7.4 en el orden del ciclo de vida, que es el orden de <c>StatusCounts</c> del contrato.</summary>
    public static readonly IReadOnlyList<string> StatusOrder =
    [
        ServiceRequestStatus.BuscandoTecnico,
        ServiceRequestStatus.EnEspera,
        ServiceRequestStatus.Asignado,
        ServiceRequestStatus.Cotizado,
        ServiceRequestStatus.CotizacionAceptada,
        ServiceRequestStatus.EnProgreso,
        ServiceRequestStatus.Completado,
        ServiceRequestStatus.Pagado,
        ServiceRequestStatus.Cancelado,
    ];

    /// <summary>Completa con 0 los estados sin solicitudes; un estado fuera de DD 7.4 no se publica en <c>byStatus</c>.</summary>
    public static ServiceRequestCounts From(IEnumerable<StatusCountRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var byStatus = StatusOrder.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        var createdToday = 0;
        foreach (var row in rows)
        {
            if (byStatus.ContainsKey(row.Status))
            {
                byStatus[row.Status] += row.Total;
            }

            createdToday += row.CreatedSince;
        }

        return new ServiceRequestCounts(byStatus, createdToday);
    }
}

/// <summary>Lista del administrador del tenant (<c>GET /v1/service-requests</c>, contrato 1.4.0).</summary>
public sealed record ListTenantServiceRequestsQuery(Guid TenantId, string? Status, int? Page, int? Size);

public sealed record TenantServiceRequestPage(IReadOnlyList<DomainServiceRequest> Items, int Page, int Size, int TotalItems);

/// <summary>
/// Solicitudes del tenant del token, de la más reciente a la más antigua (RF-18, SCRUM-38). Corre bajo el tenant del token
/// (RLS); la API entrega solo estado, identificadores y fechas, sin dirección, ubicación ni contacto (DD 8.3.15).
/// </summary>
public sealed class ListTenantServiceRequestsHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests)
{
    public async Task<TenantServiceRequestPage> HandleAsync(ListTenantServiceRequestsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (page, size) = ListFilters.Validate(query.Status, query.Page, query.Size);

        var (items, total) = await unitOfWork.ExecuteAsync(
            query.TenantId,
            ct => serviceRequests.ListForTenantAsync(query.Status, (page - 1) * size, size, ct),
            cancellationToken);
        return new TenantServiceRequestPage(items, page, size, total);
    }
}

public sealed record ServiceRequestSummary(DateTimeOffset GeneratedAt, ServiceRequestCounts Counts);

/// <summary>Conteos del tenant del token (<c>GET /v1/admin/service-requests/summary</c>, contrato 1.4.0, DD 8.3.15), bajo RLS.</summary>
public sealed class GetTenantSummaryHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, TimeProvider clock)
{
    public async Task<ServiceRequestSummary> HandleAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await unitOfWork.ExecuteAsync(
            tenantId, ct => serviceRequests.CountByStatusAsync(BogotaDay.StartUtc(now), ct), cancellationToken);
        return new ServiceRequestSummary(now, ServiceRequestCounts.From(rows));
    }
}

public sealed record TenantServiceRequestCounts(Guid TenantId, ServiceRequestCounts Counts);

public sealed record PlatformServiceRequestSummary(DateTimeOffset GeneratedAt, IReadOnlyList<TenantServiceRequestCounts> Tenants);

/// <summary>
/// Conteos de cada tenant para el <c>admin_plataforma</c> (<c>GET /v1/platform/service-requests/summary</c>, contrato 1.4.0;
/// DD 10.4, DEP-15). Solo conteos; un tenant sin solicitudes no aparece.
/// </summary>
public sealed class GetPlatformSummaryHandler(IPlatformServiceRequestCounter counter, TimeProvider clock)
{
    public async Task<PlatformServiceRequestSummary> HandleAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await counter.CountByTenantAsync(tenantId, BogotaDay.StartUtc(now), cancellationToken);
        var tenants = rows
            .GroupBy(r => r.TenantId)
            .OrderBy(g => g.Key)
            .Select(g => new TenantServiceRequestCounts(
                g.Key, ServiceRequestCounts.From(g.Select(r => new StatusCountRow(r.Status, r.Total, r.CreatedSince)))))
            .ToList();
        return new PlatformServiceRequestSummary(now, tenants);
    }
}