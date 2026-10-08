using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using QuickPatch.ServiceRequest.Infrastructure.Persistence;
using QuickPatch.ServiceRequest.UnitTests.Support;

using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace QuickPatch.ServiceRequest.IntegrationTests.Support;

/// <summary>
/// Ambiente real para las pruebas: PostgreSQL 16 + PostGIS y Kafka en contenedores, con las migraciones
/// aplicadas, los roles del DD 10.2 y el servicio conectado con el rol <c>service_request_app</c> (sujeto a RLS).
/// </summary>
public sealed class ServiceRequestEnvironment : IAsyncLifetime
{
    public const string AppPassword = "app-pruebas";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgis/postgis:16-3.5")
        .WithDatabase("db_service_request")
        .Build();

    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1")
        .Build();

    public string AdminConnectionString => postgres.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(AdminConnectionString)
    {
        Username = "service_request_app",
        Password = AppPassword,
    }.ConnectionString;

    public string KafkaServers => kafka.GetBootstrapAddress();

    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), kafka.StartAsync());

        var options = new DbContextOptionsBuilder<ServiceRequestDbContext>()
            .UseNpgsql(AdminConnectionString, n => n.UseNetTopologySuite())
            .Options;
        await using (var db = new ServiceRequestDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await ExecuteAdminAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "roles.sql")));
        await ExecuteAdminAsync($"ALTER ROLE service_request_app LOGIN PASSWORD '{AppPassword}';");

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaServers }).Build();
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification { Name = "service-request.created", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "catalog.category-changed", NumPartitions = 1, ReplicationFactor = 1 },
        ]);

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ServiceRequest", AppConnectionString);
            builder.UseSetting("Kafka:BootstrapServers", KafkaServers);
            builder.UseSetting("Outbox:PublisherRole", "service_request_outbox");
            builder.UseSetting("Outbox:PollInterval", "00:00:00.200");
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
        });
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        await Task.WhenAll(postgres.DisposeAsync().AsTask(), kafka.DisposeAsync().AsTask());
    }

    public async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAdminAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task PublishAsync(string topic, string key, string value)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = KafkaServers }).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value });
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string description, int seconds = 30)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < limit)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No se cumplió en {seconds} s: {description}");
    }
}

[CollectionDefinition(Name)]
public sealed class EnvironmentDefinition : ICollectionFixture<ServiceRequestEnvironment>
{
    public const string Name = "ambiente-real";
}