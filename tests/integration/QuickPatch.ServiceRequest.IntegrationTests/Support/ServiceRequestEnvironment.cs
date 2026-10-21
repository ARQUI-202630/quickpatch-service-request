using Confluent.Kafka;
using Confluent.Kafka.Admin;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using QuickPatch.ServiceRequest.Infrastructure.Persistence;
using QuickPatch.ServiceRequest.UnitTests.Support;

using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace QuickPatch.ServiceRequest.IntegrationTests.Support;

/// <summary>
/// Ambiente real para las pruebas: PostgreSQL 16 + PostGIS, Kafka, Redis y Garage en contenedores, con las migraciones
/// aplicadas, los roles del DD 10.2 y el servicio conectado con el rol <c>service_request_app</c> (sujeto a RLS). Redis tiene
/// el mismo usuario ACL que en QA (<c>service-request</c>, solo claves <c>service-request:*</c> y sin comandos peligrosos).
/// Garage es la misma versión que en QA, con el bucket <c>service-request-evidencias</c> y una llave que solo ve ese bucket,
/// configurado con los mismos comandos que <c>tasks/garage.yml</c> de Ansible.
/// </summary>
public sealed class ServiceRequestEnvironment : IAsyncLifetime
{
    public const string AppPassword = "app-pruebas";
    public const string RedisUser = "service-request";
    public const string RedisPassword = "redis-pruebas";
    public const string EvidenceBucket = "service-request-evidencias";

    // Valores de prueba con el formato de Garage (llave GK + 24 hexadecimales, secreto de 64).
    public const string GarageKeyId = "GK0123456789abcdef01234567";
    public const string GarageSecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const int GarageS3Port = 3900;

    private const string GarageConfig = """
        metadata_dir = "/var/lib/garage/meta"
        data_dir = "/var/lib/garage/data"
        db_engine = "sqlite"
        replication_factor = 1
        rpc_bind_addr = "127.0.0.1:3901"
        rpc_public_addr = "127.0.0.1:3901"
        rpc_secret = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"

        [s3_api]
        s3_region = "garage"
        api_bind_addr = "0.0.0.0:3900"
        root_domain = ".s3.garage.localhost"
        """;

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgis/postgis:16-3.5")
        .WithDatabase("db_service_request")
        .Build();

    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1")
        .Build();

    private readonly RedisContainer redis = new RedisBuilder("redis:7.4-alpine")
        .Build();

    private readonly IContainer garage = new ContainerBuilder("dxflrs/garage:v2.4.1")
        .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(GarageConfig), "/etc/garage.toml")
        .WithPortBinding(GarageS3Port, true)
        // La imagen no trae shell: se espera el mensaje de arranque de la API S3.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S3 API server listening"))
        .Build();

    public string GarageEndpoint => $"http://{garage.Hostname}:{garage.GetMappedPublicPort(GarageS3Port)}";

    public string RedisEndpoint => $"{redis.Hostname}:{redis.GetMappedPublicPort(RedisBuilder.RedisPort)}";

    public string AdminConnectionString => postgres.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(AdminConnectionString)
    {
        Username = "service_request_app",
        Password = AppPassword,
    }.ConnectionString;

    public string KafkaServers => kafka.GetBootstrapAddress();

    public FakeIdentityServer Identity { get; } = new();

    public FakeNominatimServer Nominatim { get; } = new();

    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            postgres.StartAsync(), kafka.StartAsync(), redis.StartAsync(), garage.StartAsync(), Identity.StartAsync(), Nominatim.StartAsync());
        await ConfigureGarageAsync();

        // Igual que templates/redis-users.acl.j2 de Ansible.
        var acl = await redis.ExecAsync(["redis-cli", "ACL", "SETUSER", RedisUser, "on", $">{RedisPassword}",
            "~service-request:*", "&service-request:*", "+@all", "-@dangerous"]);
        if (acl.ExitCode != 0)
        {
            throw new InvalidOperationException($"No se pudo crear el usuario de Redis: {acl.Stderr}");
        }

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
            new TopicSpecification { Name = "matching.technician-assigned", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "matching.no-technician-available", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "matching.search-resumed", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "payment.approved", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "payment.rejected", NumPartitions = 1, ReplicationFactor = 1 },
        ]);

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ServiceRequest", AppConnectionString);
            builder.UseSetting("Kafka:BootstrapServers", KafkaServers);
            builder.UseSetting("Outbox:PublisherRole", "service_request_outbox");
            builder.UseSetting("Platform:ReadRole", "service_request_platform");
            builder.UseSetting("Quotes:SchedulerRole", "service_request_scheduler");
            builder.UseSetting("Quotes:ExpiryInterval", "00:00:01");
            builder.UseSetting("Redis:Endpoint", RedisEndpoint);
            builder.UseSetting("Redis:User", RedisUser);
            builder.UseSetting("Redis:Password", RedisPassword);
            builder.UseSetting("Outbox:PollInterval", "00:00:00.200");
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
            builder.UseSetting("Identity:BaseUrl", Identity.BaseUrl);
            builder.UseSetting("Geocoding:NominatimUrl", Nominatim.BaseUrl);
            builder.UseSetting("Evidence:Endpoint", GarageEndpoint);
            builder.UseSetting("Evidence:AccessKey", GarageKeyId);
            builder.UseSetting("Evidence:SecretKey", GarageSecret);
        });
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        await Task.WhenAll(postgres.DisposeAsync().AsTask(), kafka.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask(),
            garage.DisposeAsync().AsTask(),
            Identity.DisposeAsync().AsTask(),
            Nominatim.DisposeAsync().AsTask());
    }

    /// <summary>Nodo único en el layout, llave importada, bucket y permiso de la llave solo sobre ese bucket.</summary>
    private async Task ConfigureGarageAsync()
    {
        var nodeId = string.Empty;
        for (var attempt = 0; attempt < 30 && nodeId.Length < 16; attempt++)
        {
            var id = await garage.ExecAsync(["/garage", "node", "id", "-q"]);
            nodeId = id.ExitCode == 0 ? id.Stdout.Trim().Split('@')[0] : string.Empty;
            if (nodeId.Length < 16)
            {
                await Task.Delay(500);
            }
        }

        await GarageAsync("layout", "assign", "-z", "dc1", "-c", "1G", nodeId[..16]);
        await GarageAsync("layout", "apply", "--version", "1");
        await GarageAsync("key", "import", "--yes", "-n", "service-request", GarageKeyId, GarageSecret);
        await GarageAsync("bucket", "create", EvidenceBucket);
        await GarageAsync("bucket", "allow", "--read", "--write", EvidenceBucket, "--key", "service-request");
    }

    private async Task GarageAsync(params string[] args)
    {
        var result = await garage.ExecAsync(["/garage", .. args]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"garage {string.Join(' ', args)} falló: {result.Stderr}");
        }
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