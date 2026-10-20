using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Infrastructure.Options;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>
/// Busca las cotizaciones pendientes ya vencidas de todos los tenants (RN-Q6). Abre una transacción de solo lectura y adopta
/// <c>service_request_scheduler</c> con <c>SET LOCAL ROLE</c> (P-06, DD 10.2: <c>BYPASSRLS</c>, solo <c>SELECT</c> sobre
/// identificador, tenant, solicitud, estado y vencimiento). No modifica nada: cada cotización se vence después en una
/// transacción con el rol de la aplicación y el tenant de la fila.
/// </summary>
public sealed partial class ExpiredQuoteFinder(ServiceRequestDbContext db, IOptions<QuotesOptions> options) : IExpiredQuoteFinder
{
    public async Task<IReadOnlyList<ExpiredQuoteRef>> FindAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var role = options.Value.SchedulerRole;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!RoleName().IsMatch(role))
            {
                throw new InvalidOperationException("Quotes:SchedulerRole no es un nombre de rol válido.");
            }

#pragma warning disable EF1002 // El nombre del rol se validó con una expresión regular estricta; no admite parámetros.
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE \"{role}\"", cancellationToken);
#pragma warning restore EF1002
        }

        // Solo columnas que el rol puede leer. Sin el rol (desarrollo local), RLS sin tenant fijado no deja ver ninguna.
        var rows = await db.Database.SqlQuery<ExpiredQuoteRef>($"""
            SELECT id AS "QuoteId", tenant_id AS "TenantId", service_request_id AS "ServiceRequestId"
            FROM quotes WHERE status = 'pendiente' AND expires_at <= {now}
            ORDER BY expires_at LIMIT {limit}
            """).ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();
}