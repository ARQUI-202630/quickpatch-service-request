namespace QuickPatch.ServiceRequest.Domain.Categories;

/// <summary>
/// Copia local de una categoría de Catalog (DD, tabla <c>service_request_categories</c>),
/// alimentada por el evento <c>catalog.category-changed</c>.
/// </summary>
public sealed class CategoryReplica
{
    public CategoryReplica(Guid categoryId, Guid tenantId, string name, bool active, DateTimeOffset updatedAt)
    {
        CategoryId = categoryId;
        TenantId = tenantId;
        Name = name;
        Active = active;
        UpdatedAt = updatedAt;
    }

    public Guid CategoryId { get; }

    public Guid TenantId { get; }

    public string Name { get; private set; }

    public bool Active { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Aplica el estado recibido solo si es más reciente que el guardado; un evento atrasado se descarta.
    /// </summary>
    /// <returns><c>true</c> si la copia cambió.</returns>
    public bool ApplyChange(string name, bool active, DateTimeOffset updatedAt)
    {
        if (updatedAt <= UpdatedAt)
        {
            return false;
        }

        Name = name;
        Active = active;
        UpdatedAt = updatedAt;
        return true;
    }
}