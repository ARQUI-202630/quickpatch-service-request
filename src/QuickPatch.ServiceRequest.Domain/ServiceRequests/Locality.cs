namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>
/// Localidad de Bogotá (DD 5.18). ServiceRequest tiene una copia de solo lectura del catálogo de Matching para la vista
/// previa de la ubicación (P-09): los mismos códigos de <c>GET /v1/localities</c>.
/// </summary>
public sealed record Locality(string Code, string Name);