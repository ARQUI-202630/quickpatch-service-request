namespace QuickPatch.ServiceRequest.Api.Security;

/// <summary>
/// Tenant del canal (sección <c>Channel</c>), la misma configuración que usa Identity en el registro y el login (RN-U5).
/// Solo lo usa la lista de categorías sin token, para que la app muestre las especialidades en el registro de técnicos
/// (RF-02, contrato 1.2.0). Cada canal (<c>X-Channel-Id</c>, DD 8.1) apunta a su tenant en <see cref="Tenants"/>
/// (DEP-09); sin cabecera se usa <see cref="TenantId"/>. Sin tenant, la lista sin token responde 503.
/// </summary>
public sealed class ChannelOptions
{
    public const string Section = "Channel";

    /// <summary>Tenant de las peticiones sin <c>X-Channel-Id</c>.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Canal → tenant (<c>Channel__Tenants__quickpatch-mobile</c>). El canal no distingue mayúsculas.</summary>
    public Dictionary<string, Guid> Tenants { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tenant del canal: el del mapa si llega <paramref name="channelId"/>, o <see cref="TenantId"/> si no llega.
    /// Con el mapa vacío (un solo tenant, DD 10.3) todos los canales van a <see cref="TenantId"/>; con el mapa
    /// configurado, un canal que no está en él no cae en el tenant por defecto.
    /// </summary>
    public bool TryResolve(string? channelId, out Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || Tenants.Count == 0)
        {
            tenantId = TenantId;
        }
        else if (!Tenants.TryGetValue(channelId.Trim(), out tenantId))
        {
            tenantId = Guid.Empty;
        }

        return tenantId != Guid.Empty;
    }
}