namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>Quién canceló la solicitud (<c>cancelled_by_type</c>, RN-SR8).</summary>
public static class CancellationActor
{
    public const string Cliente = "cliente";
    public const string AdminTenant = "admin_tenant";
    public const string Sistema = "sistema";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Cliente, AdminTenant, Sistema };
}