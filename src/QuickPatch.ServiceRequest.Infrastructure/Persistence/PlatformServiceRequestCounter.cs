using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Infrastructure.Options;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>
/// Conteos de todos los tenants para el <c>admin_plataforma</c> (DD 10.4, DEP-15). Abre una transacción de solo lectura,
/// adopta <c>service_request_platform</c> con <c>SET LOCAL ROLE</c> (DD 10.2: <c>BYPASSRLS</c>, solo <c>SELECT</c> sobre
/// <c>tenant_id</c>, <c>status</c> y <c>created_at</c>) y agrupa en la base: no trae ninguna fila de solicitud a memoria.
/// El rol vuelve al de la conexión al terminar la transacción.
/// </summary>
public sealed partial class PlatformServiceRequestCounter(ServiceRequestDbContext db, IOptions<PlatformOptions> options)
    : IPlatformServiceRequestCounter
{
    public async Task<IReadOnlyList<TenantStatusCountRow>> CountByTenantAsync(
        Guid? tenantId, DateTimeOffset createdSince, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var role = options.Value.ReadRole;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!RoleName().IsMatch(role))
            {
                throw new InvalidOperationException("Platform:ReadRole no es un nombre de rol válido.");
            }

#pragma warning disable EF1002 // El nombre del rol se validó con una expresión regular estricta; no admite parámetros.
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE \"{role}\"", cancellationToken);
#pragma warning restore EF1002
        }

        // Solo columnas que el rol puede leer. Sin el rol (desarrollo local) RLS sigue activa y, sin tenant fijado, no ve nada.
        var rows = tenantId is { } tenant
            ? await db.Database.SqlQuery<TenantStatusCountRow>($"""
                SELECT tenant_id AS "TenantId", status AS "Status", count(*)::int AS "Total",
                       (count(*) FILTER (WHERE created_at >= {createdSince}))::int AS "CreatedSince"
                FROM service_requests WHERE tenant_id = {tenant} GROUP BY tenant_id, status
                """).ToListAsync(cancellationToken)
            : await db.Database.SqlQuery<TenantStatusCountRow>($"""
                SELECT tenant_id AS "TenantId", status AS "Status", count(*)::int AS "Total",
                       (count(*) FILTER (WHERE created_at >= {createdSince}))::int AS "CreatedSince"
                FROM service_requests GROUP BY tenant_id, status
                """).ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();
}