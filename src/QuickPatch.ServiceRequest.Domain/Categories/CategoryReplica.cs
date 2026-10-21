namespace QuickPatch.ServiceRequest.Domain.Categories;

/// <summary>
/// Copia local de una categoría de Catalog (DD, tabla <c>service_request_categories</c>),
/// alimentada por el evento <c>catalog.category-changed</c>. Desde ADR-025 también sirve la lista de categorías de las apps
/// (contrato 1.8.0), por eso guarda la descripción.
/// </summary>
public sealed class CategoryReplica
{
    public CategoryReplica(
        Guid categoryId, Guid tenantId, string name, bool active, DateTimeOffset updatedAt, string? description = null)
    {
        CategoryId = categoryId;
        TenantId = tenantId;
        Name = name;
        Active = active;
        UpdatedAt = updatedAt;
        Description = description;
    }

    public Guid CategoryId { get; }

    public Guid TenantId { get; }

    public string Name { get; private set; }

    /// <summary>Descripción de la categoría; null si no tiene o si el evento es anterior a quickpatch-kafka v0.8.0.</summary>
    public string? Description { get; private set; }

    public bool Active { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Aplica el estado recibido solo si es más reciente que el guardado; un evento atrasado se descarta.
    /// </summary>
    /// <returns><c>true</c> si la copia cambió.</returns>
    public bool ApplyChange(string name, bool active, DateTimeOffset updatedAt, string? description = null)
    {
        if (updatedAt <= UpdatedAt)
        {
            return false;
        }

        Name = name;
        Description = description;
        Active = active;
        UpdatedAt = updatedAt;
        return true;
    }
}