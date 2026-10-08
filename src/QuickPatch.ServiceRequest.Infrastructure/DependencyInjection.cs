using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
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
        services.AddScoped<IProcessedEventRepository, ProcessedEventRepository>();
        services.AddScoped<IOutbox, EfOutbox>();

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
        }

        return services;
    }
}