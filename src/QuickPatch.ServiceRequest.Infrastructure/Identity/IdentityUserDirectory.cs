using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;

namespace QuickPatch.ServiceRequest.Infrastructure.Identity;

/// <summary>Sección <c>Identity</c>: dirección interna de Identity Service y tiempo de espera de la consulta.</summary>
public sealed class IdentityOptions
{
    public const string Section = "Identity";

    /// <summary>
    /// Por ejemplo <c>http://identity.quickpatch.svc.cluster.local:8080</c>. Vacío: no se consulta y los nombres
    /// llegan en null (desarrollo local sin Identity).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Corto a propósito: el detalle no debe esperar a Identity más que esto (DD V4 8.3.4).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Cliente de <c>GET /internal/v1/users</c> (contrato <c>identity-internal.v1.yaml</c> 1.0.0). Reenvía el JWT del
/// usuario y el <c>X-Correlation-Id</c>; ante cualquier falla devuelve un resultado vacío y registra el motivo, nunca el token.
/// </summary>
public sealed partial class IdentityUserDirectory(HttpClient http, ILogger<IdentityUserDirectory> logger) : IUserDirectory
{
    public const string LookupPath = "internal/v1/users";

    private static readonly IReadOnlyDictionary<Guid, UserContact> Empty = new Dictionary<Guid, UserContact>();

    public async Task<IReadOnlyDictionary<Guid, UserContact>> LookupAsync(
        IReadOnlyCollection<Guid> userIds, string accessToken, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (http.BaseAddress is null || userIds.Count == 0 || string.IsNullOrEmpty(accessToken))
        {
            return Empty;
        }

        var query = string.Join('&', userIds.Distinct().Select(id => $"ids={id}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{LookupPath}?{query}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, $"HTTP {(int)response.StatusCode}", null);
                return Empty;
            }

            var body = await response.Content.ReadFromJsonAsync<LookupResponse>(EventSerializer.Options, cancellationToken);
            return body?.Items?
                .Where(u => u.Id != Guid.Empty)
                .GroupBy(u => u.Id)
                .ToDictionary(g => g.Key, g => new UserContact(g.Key, g.First().FullName ?? string.Empty, g.First().Phone))
                ?? Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // TaskCanceledException sin cancelación del llamador es el tiempo de espera del HttpClient.
            LogFailed(logger, ex.GetType().Name, ex);
            return Empty;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudieron leer los nombres de Identity ({Reason}); el detalle sale sin ellos.")]
    private static partial void LogFailed(ILogger logger, string reason, Exception? exception);

    private sealed record LookupResponse(IReadOnlyList<LookupItem>? Items);

    private sealed record LookupItem(Guid Id, string? FullName, string? Phone);
}