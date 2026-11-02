namespace QuickPatch.ServiceRequest.Domain.Tenants;

/// <summary>
/// Copia local del estado de un tenant (tabla <c>tenant_status_replica</c>), alimentada por el evento
/// <c>tenant.status-changed</c> de Identity (RN-T1, ADR-017). Sirve para rechazar la creación de solicitudes de un tenant
/// desactivado sin llamar a Identity (ADR-003) y sin esperar a que venza el token ya emitido.
/// Un tenant sin fila se considera activo: la copia empieza vacía y solo guarda los tenants que han cambiado de estado.
/// </summary>
public sealed class TenantStatusReplica
{
    public const string Active = "activo";
    public const string Inactive = "inactivo";

    public TenantStatusReplica(Guid tenantId, string status, DateTimeOffset updatedAt)
    {
        TenantId = tenantId;
        Status = status;
        UpdatedAt = updatedAt;
    }

    public Guid TenantId { get; }

    public string Status { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsInactive => Status == Inactive;

    public static bool IsValidStatus(string? status) => status is Active or Inactive;

    /// <summary>
    /// Aplica el estado recibido solo si es más reciente que el guardado; un evento atrasado se descarta (ADR-017).
    /// </summary>
    /// <returns><c>true</c> si la copia cambió.</returns>
    public bool ApplyChange(string status, DateTimeOffset updatedAt)
    {
        if (updatedAt <= UpdatedAt)
        {
            return false;
        }

        Status = status;
        UpdatedAt = updatedAt;
        return true;
    }
}