using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Infrastructure.Options;
using QuickPatch.Transversal.Persistence;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>
/// Busca las cotizaciones pendientes ya vencidas de todos los tenants (RN-Q6). Abre una transacción de solo lectura y adopta
/// <c>service_request_scheduler</c> con <c>SET LOCAL ROLE</c> (P-06, DD 10.2: <c>BYPASSRLS</c>, solo <c>SELECT</c> sobre
/// identificador, tenant, solicitud, estado y vencimiento). No modifica nada: cada cotización se vence después en una
/// transacción con el rol de la aplicación y el tenant de la fila.
/// </summary>
public sealed class ExpiredQuoteFinder(ServiceRequestDbContext db, IOptions<QuotesOptions> options) : IExpiredQuoteFinder
{
    public async Task<IReadOnlyList<ExpiredQuoteRef>> FindAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var role = options.Value.SchedulerRole;
        // Rol validado y adoptado con la implementación común (ADR-020); vacío solo en desarrollo local.
        await SessionRole.SetAsync(db, role, cancellationToken);

        // Solo columnas que el rol puede leer. Sin el rol (desarrollo local), RLS sin tenant fijado no deja ver ninguna.
        var rows = await db.Database.SqlQuery<ExpiredQuoteRef>($"""
            SELECT id AS "QuoteId", tenant_id AS "TenantId", service_request_id AS "ServiceRequestId"
            FROM quotes WHERE status = 'pendiente' AND expires_at <= {now}
            ORDER BY expires_at LIMIT {limit}
            """).ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}