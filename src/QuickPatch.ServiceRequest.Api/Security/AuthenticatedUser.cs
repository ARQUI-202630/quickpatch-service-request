using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace QuickPatch.ServiceRequest.Api.Security;

/// <summary>
/// Contexto autenticado (SDD: <c>AuthenticatedContext</c>) leído de los claims del JWT emitido por Identity:
/// <c>sub</c> (userId), <c>tenant_id</c> (tenantId) y <c>role</c>. El tenant nunca se toma del cuerpo ni de cabeceras.
/// </summary>
public sealed record AuthenticatedUser(Guid UserId, Guid TenantId, string Role)
{
    public const string SubjectClaim = "sub";
    public const string TenantClaim = "tenant_id";
    public const string RoleClaim = "role";
    public const string ClientRole = "cliente";
    public const string ClientPolicy = "SoloCliente";

    public static bool TryFrom(ClaimsPrincipal principal, out AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(principal);
        user = null!;
        if (!Guid.TryParse(principal.FindFirstValue(SubjectClaim), out var userId) || userId == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue(TenantClaim), out var tenantId) || tenantId == Guid.Empty)
        {
            return false;
        }

        user = new AuthenticatedUser(userId, tenantId, principal.FindFirstValue(RoleClaim) ?? string.Empty);
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
            .AddPolicy(AuthenticatedUser.ClientPolicy, policy => policy.RequireRole(AuthenticatedUser.ClientRole));
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