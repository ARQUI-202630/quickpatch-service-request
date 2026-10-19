using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Infrastructure.Geocoding;
using QuickPatch.ServiceRequest.Infrastructure.Identity;
using QuickPatch.ServiceRequest.Infrastructure.Messaging;
using QuickPatch.ServiceRequest.Infrastructure.Options;
using QuickPatch.ServiceRequest.Infrastructure.Persistence;

namespace QuickPatch.ServiceRequest.Infrastructure;

/// <summary>
/// Capa de infraestructura: PostgreSQL + PostGIS (EF Core), Kafka y adaptadores externos (SDD 6.3).
/// Implementa las interfaces que definen Application y Domain.
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "ServiceRequest";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CoverageOptions>(configuration.GetSection(CoverageOptions.Section));
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.Section));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.Section));
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.Section));

        // Consulta interna por lote a Identity (identity-internal.v1.yaml, DD V4 8.3.4).
        var identity = configuration.GetSection(IdentityOptions.Section).Get<IdentityOptions>() ?? new IdentityOptions();
        services.AddHttpClient<IUserDirectory, IdentityUserDirectory>(http =>
        {
            if (!string.IsNullOrWhiteSpace(identity.BaseUrl))
            {
                http.BaseAddress = new Uri(identity.BaseUrl.TrimEnd('/') + "/");
            }

            http.Timeout = identity.Timeout;
        });

        // Nominatim propio de la VM de datos (ADR-024, DD 8.3.3); la espera total la controla el geocodificador.
        var geocoding = configuration.GetSection(GeocodingOptions.Section).Get<GeocodingOptions>() ?? new GeocodingOptions();
        services.AddSingleton(geocoding);
        services.AddHttpClient<IGeocoder, NominatimGeocoder>(http =>
        {
            if (!string.IsNullOrWhiteSpace(geocoding.NominatimUrl))
            {
                http.BaseAddress = new Uri(geocoding.NominatimUrl.TrimEnd('/') + "/");
            }

            http.Timeout = geocoding.Timeout + TimeSpan.FromSeconds(1);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("quickpatch-service-request");
        });

        services.AddSingleton(sp =>
        {
            var c = sp.GetRequiredService<IOptions<CoverageOptions>>().Value;
            return new CoverageArea(c.MinLatitude, c.MaxLatitude, c.MinLongitude, c.MaxLongitude);
        });

        services.AddDbContext<ServiceRequestDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString(ConnectionStringName),
                npgsql => npgsql.UseNetTopologySuite()));

        services.AddScoped<ITenantUnitOfWork, TenantUnitOfWork>();
        services.AddScoped<IServiceRequestRepository, ServiceRequestRepository>();
        services.AddScoped<ICategoryReplicaRepository, CategoryReplicaRepository>();
        services.AddScoped<ILocalityCatalog, LocalityCatalog>();
        services.AddScoped<IProcessedEventRepository, ProcessedEventRepository>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<IPlatformServiceRequestCounter, PlatformServiceRequestCounter>();

        services.AddHealthChecks().AddDbContextCheck<ServiceRequestDbContext>("postgresql", tags: [ReadyTag]);

        var kafka = configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>() ?? new KafkaOptions();
        if (kafka.Enabled)
        {
            services.AddSingleton(_ => new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = kafka.BootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,
            }).Build());

            services.AddSingleton(_ => new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = kafka.BootstrapServers,
                GroupId = kafka.ConsumerGroupId,
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
            }).Build());

            services.AddSingleton<OutboxPublisher>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher>());
            services.AddSingleton<CategoryChangedConsumer>();
            services.AddHostedService(sp => sp.GetRequiredService<CategoryChangedConsumer>());

            // Consumidor propio: un IConsumer solo puede tener una suscripción activa.
            services.AddSingleton(sp => new MatchingEventsConsumer(
                sp.GetRequiredService<IServiceScopeFactory>(),
                new ConsumerBuilder<string, string>(new ConsumerConfig
                {
                    BootstrapServers = kafka.BootstrapServers,
                    GroupId = kafka.ConsumerGroupId,
                    EnableAutoCommit = false,
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                }).Build(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MatchingEventsConsumer>>()));
            services.AddHostedService(sp => sp.GetRequiredService<MatchingEventsConsumer>());
        }

        return services;
    }
}