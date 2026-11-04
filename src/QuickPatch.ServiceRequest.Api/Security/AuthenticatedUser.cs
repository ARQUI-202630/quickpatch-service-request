using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace QuickPatch.ServiceRequest.Api.Security;

/// <summary>
/// Contexto autenticado (SDD: <c>AuthenticatedContext</c>) leído de los claims del JWT emitido por Identity:
/// <c>sub</c> (userId), <c>tenant_id</c> (tenantId), <c>role</c> y, para el <c>empresa_contacto</c>, <c>company_id</c>
/// (identity 1.8.0). El tenant y la empresa nunca se toman del cuerpo ni de cabeceras.
/// </summary>
public sealed record AuthenticatedUser(Guid UserId, Guid TenantId, string Role, Guid? CompanyId = null)
{
    public const string SubjectClaim = "sub";
    public const string TenantClaim = "tenant_id";
    public const string RoleClaim = "role";
    public const string CompanyClaim = "company_id";
    public const string ClientRole = "cliente";
    /// <summary>
    /// Quien crea la solicitud y actúa como su dueño: el <c>cliente</c> y, desde 1.9.0, el <c>empresa_contacto</c> (RF-08).
    /// Cada caso de uso compara además el usuario con <c>client_id</c>.
    /// </summary>
    public const string ClientPolicy = "SoloCliente";

    /// <summary>Lista de solicitudes (contrato 1.9.0): el <c>admin_tenant</c>, el <c>cliente</c> y el <c>empresa_contacto</c>.</summary>
    public const string ListPolicy = "ListaDeSolicitudes";
    public const string TechnicianRole = "tecnico";
    public const string CompanyContactRole = "empresa_contacto";

    /// <summary>Vista previa de la ubicación (contrato 1.3.0): los roles que crean solicitudes.</summary>
    public const string PreviewPolicy = "CreaSolicitudes";

    /// <summary>Lista de solicitudes del técnico (contrato 1.2.0).</summary>
    public const string TechnicianPolicy = "SoloTecnico";

    public const string AdminTenantRole = "admin_tenant";
    public const string PlatformAdminRole = "admin_plataforma";

    /// <summary>Cancelación (contrato 1.5.0, RN-SR8): el cliente dueño y el administrador del tenant.</summary>
    public const string CancelPolicy = "ClienteOAdminTenant";

    /// <summary>Lista y conteos del panel del tenant (contrato 1.4.0).</summary>
    public const string AdminTenantPolicy = "SoloAdminTenant";

    /// <summary>Conteos de todos los tenants (contrato 1.4.0, DD 10.4).</summary>
    public const string PlatformAdminPolicy = "SoloAdminPlataforma";

    /// <summary>Consulta del detalle (contrato 1.1.0): el cliente dueño y el técnico asignado.</summary>
    public const string ReadPolicy = "ClienteOTecnico";

    /// <summary>Evidencias (contrato 1.7.0): el cliente dueño, el técnico asignado y el <c>admin_tenant</c>.</summary>
    public const string EvidenceReadPolicy = "ClienteTecnicoOAdminTenant";

    public static bool TryFrom(ClaimsPrincipal principal, out AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(principal);
        user = null!;
        if (!Guid.TryParse(principal.FindFirstValue(SubjectClaim), out var userId) || userId == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue(TenantClaim), out var tenantId) || tenantId == Guid.Empty)
        {
            return false;
        }

        Guid? companyId = Guid.TryParse(principal.FindFirstValue(CompanyClaim), out var company) && company != Guid.Empty ? company : null;
        user = new AuthenticatedUser(userId, tenantId, principal.FindFirstValue(RoleClaim) ?? string.Empty, companyId);
        return true;
    }
}

/// <summary>Configuración del JWT (sección <c>Jwt</c>). Identity firma con RS256; este servicio solo tiene la llave pública.</summary>
public sealed class JwtSettings
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "quickpatch-identity";

    public string Audience { get; set; } = "quickpatch";

    /// <summary>Llave pública RSA en formato PEM.</summary>
    public string PublicKeyPem { get; set; } = string.Empty;
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddQuickPatchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(JwtSettings.Section).Get<JwtSettings>() ?? new JwtSettings();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = LoadPublicKey(settings.PublicKeyPem),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = AuthenticatedUser.SubjectClaim,
                    RoleClaimType = AuthenticatedUser.RoleClaim,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthenticatedUser.ClientPolicy, policy => policy.RequireRole(AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole))
            .AddPolicy(AuthenticatedUser.ReadPolicy, policy => policy.RequireRole(
                AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole, AuthenticatedUser.TechnicianRole))
            .AddPolicy(AuthenticatedUser.EvidenceReadPolicy, policy => policy.RequireRole(
                AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole, AuthenticatedUser.TechnicianRole, AuthenticatedUser.AdminTenantRole))
            .AddPolicy(AuthenticatedUser.ListPolicy, policy => policy.RequireRole(
                AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole, AuthenticatedUser.AdminTenantRole))
            .AddPolicy(AuthenticatedUser.TechnicianPolicy, policy => policy.RequireRole(AuthenticatedUser.TechnicianRole))
            .AddPolicy(AuthenticatedUser.PreviewPolicy, policy => policy.RequireRole(AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole))
            .AddPolicy(AuthenticatedUser.CancelPolicy, policy => policy.RequireRole(
                AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole, AuthenticatedUser.AdminTenantRole))
            .AddPolicy(AuthenticatedUser.AdminTenantPolicy, policy => policy.RequireRole(AuthenticatedUser.AdminTenantRole))
            .AddPolicy(AuthenticatedUser.PlatformAdminPolicy, policy => policy.RequireRole(AuthenticatedUser.PlatformAdminRole));
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ForbiddenAuditHandler>();

        return services;
    }

    private static RsaSecurityKey? LoadPublicKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            // Sin llave no se valida ningún token: todas las peticiones protegidas responden 401 (falla cerrado).
            return null;
        }

#pragma warning disable CA2000 // La llave vive lo que vive la aplicación.
        var rsa = RSA.Create();
#pragma warning restore CA2000
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa);
    }
}