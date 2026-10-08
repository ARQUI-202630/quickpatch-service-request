namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>Estados de la solicitud (DD, sección 7.4). Se guardan como texto en <c>service_requests.status</c>.</summary>
public static class ServiceRequestStatus
{
    public const string BuscandoTecnico = "buscando_tecnico";
    public const string EnEspera = "en_espera";
    public const string Asignado = "asignado";
    public const string Cotizado = "cotizado";
    public const string CotizacionAceptada = "cotizacion_aceptada";
    public const string EnProgreso = "en_progreso";
    public const string Completado = "completado";
    public const string Pagado = "pagado";
    public const string Cancelado = "cancelado";
}