namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>
/// Localidad de Bogotá (DD 5.18). ServiceRequest tiene una copia de solo lectura del catálogo de Matching para la vista
/// previa de la ubicación (P-09) y, desde ADR-025, para la lista de <c>GET /v1/service-requests/localities</c>.
/// </summary>
public sealed record Locality(string Code, string Name);