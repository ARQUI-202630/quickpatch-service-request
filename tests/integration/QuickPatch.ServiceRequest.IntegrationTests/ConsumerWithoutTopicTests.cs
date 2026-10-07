using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.ServiceRequest.Infrastructure.Messaging;

using Testcontainers.Kafka;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// Hallazgo de POC-BE-001: si <c>catalog.category-changed</c> todavía no existe (Catalog no ha publicado nada),
/// el consumidor no debe detener el servicio. Kafka propio y sin topics creados.
/// </summary>
public sealed class ConsumerWithoutTopicTests : IAsyncLifetime
{
    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();

    public Task InitializeAsync() => kafka.StartAsync();

    public Task DisposeAsync() => kafka.DisposeAsync().AsTask();

    [Fact]
    public async Task TopicInexistente_ElConsumidorSigueVivoYReintenta()
    {
        var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.GetBootstrapAddress(),
            GroupId = "service-request-service",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        using var service = new CategoryChangedConsumer(scopes, consumer, NullLogger<CategoryChangedConsumer>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask!.IsCompleted, "El consumidor terminó en lugar de reintentar.");

        await service.StopAsync(CancellationToken.None);
    }
}