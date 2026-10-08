using System.Security.Cryptography;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace QuickPatch.ServiceRequest.UnitTests.Support;

/// <summary>Emite JWT RS256 como lo haría Identity, con una llave generada para las pruebas.</summary>
public static class TestTokens
{
    public const string Issuer = "quickpatch-identity";
    public const string Audience = "quickpatch";

    private static readonly RSA Key = RSA.Create(2048);

    public static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();

    public static string Create(Guid userId, Guid tenantId, string role, DateTimeOffset? expires = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["tenant_id"] = tenantId.ToString(),
            ["role"] = role,
        };

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            Expires = (expires ?? DateTimeOffset.UtcNow.AddMinutes(10)).UtcDateTime,
            NotBefore = (expires ?? DateTimeOffset.UtcNow.AddMinutes(10)).UtcDateTime.AddMinutes(-20),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(Key), SecurityAlgorithms.RsaSha256),
        });
    }
}