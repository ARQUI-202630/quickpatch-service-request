namespace QuickPatch.ServiceRequest.Infrastructure.Options;

/// <summary>Área de cobertura (RN-SR9). Sección <c>Coverage</c>; por defecto, Bogotá urbana.</summary>
public sealed class CoverageOptions
{
    public const string Section = "Coverage";

    public double MinLatitude { get; set; } = 4.45;

    public double MaxLatitude { get; set; } = 4.85;

    public double MinLongitude { get; set; } = -74.25;

    public double MaxLongitude { get; set; } = -73.98;
}

/// <summary>Conexión a Kafka (VM6). Sección <c>Kafka</c>; sin servidores, no arrancan productor ni consumidor.</summary>
public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public string ConsumerGroupId { get; set; } = "service-request-service";

    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Publicador del Outbox (ADR-007). Sección <c>Outbox</c>.</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>
    /// Rol <c>BYPASSRLS</c> que adopta el publicador con <c>SET LOCAL ROLE</c> para leer los eventos de todos
    /// los tenants (DD, sección 10.2). Vacío solo en desarrollo local, cuando el usuario de la conexión ya puede leerlos.
    /// </summary>
    public string PublisherRole { get; set; } = string.Empty;

    public int BatchSize { get; set; } = 50;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}