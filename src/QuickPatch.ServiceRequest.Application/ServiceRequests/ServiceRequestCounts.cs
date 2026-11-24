using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

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