using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Matching;
using QuickPatch.ServiceRequest.Infrastructure.Messaging;
using QuickPatch.ServiceRequest.Infrastructure.Options;
using QuickPatch.ServiceRequest.Infrastructure.Scheduling;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.UnitTests.Infrastructure;

/// <summary>
/// Un <see cref="OperationCanceledException"/> solo es un apagado si el token del servicio está cancelado. Un
/// <see cref="TaskCanceledException"/> sin cancelación (el tiempo de espera de Redis, de la base o de un servicio) es un fallo de
/// esa vuelta o de ese mensaje: se reintenta. En producción (4 de noviembre de 2026) el filtro
/// <c>when (ex is not OperationCanceledException)</c> lo dejó subir desde <c>QuoteExpirer</c>, el <c>BackgroundService</c> falló y
/// <c>HostOptions.BackgroundServiceExceptionBehavior = StopHost</c> apagó el servicio entero.
/// </summary>
public class BackgroundServiceCancellationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Candado que lanza un TaskCanceledException sin cancelación las primeras veces, como un tiempo de espera de Redis.</summary>
    private sealed class LockConTiempoAgotado(int fallos) : IDistributedLock
    {
        private int llamadas;

        public int Llamadas => Volatile.Read(ref llamadas);

        public Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref llamadas) <= fallos)
            {
                throw new TaskCanceledException("Tiempo de espera agotado (sin cancelación del llamador).");
            }

            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    /// <summary>Candado que se queda esperando hasta que se cancela el token del servicio, como una vuelta en curso al apagar.</summary>
    private sealed class LockQueEsperaAlApagar : IDistributedLock
    {
        public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    /// <summary>Unidad de trabajo cuyo trabajo agota el tiempo de espera, como la base de datos o un servicio que no responde.</summary>
    private sealed class UnidadDeTrabajoConTiempoAgotado : ITenantUnitOfWork
    {
        public Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("Tiempo de espera agotado (sin cancelación del llamador).");
    }

    private static QuoteExpirer Expirer(IDistributedLock distributedLock) => new(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        distributedLock,
        Options.Create(new QuotesOptions { ExpiryInterval = TimeSpan.FromMilliseconds(15) }),
        TimeProvider.System,
        NullLogger<QuoteExpirer>.Instance);

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicion() && DateTime.UtcNow < limite)
        {
            await Task.Delay(10);
        }

        Assert.True(condicion(), "No se cumplió la condición dentro del plazo.");
    }

    [Fact]
    public async Task QuoteExpirer_ConTiempoAgotadoEnElCandado_SigueVivoYReintenta()
    {
        var candado = new LockConTiempoAgotado(fallos: 3);
        using var expirer = Expirer(candado);

        await expirer.StartAsync(CancellationToken.None);
        await EsperarAsync(() => candado.Llamadas >= 5);

        // Antes del arreglo la tarea ya estaba fallida tras la primera llamada y el host se habría apagado.
        Assert.False(expirer.ExecuteTask!.IsCompleted);

        await expirer.StopAsync(CancellationToken.None);
        Assert.True(expirer.ExecuteTask.IsCompleted);
    }

    [Fact]
    public async Task QuoteExpirer_AlApagar_TerminaPorLaCancelacionDelToken()
    {
        using var expirer = Expirer(new LockQueEsperaAlApagar());

        await expirer.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(expirer.ExecuteTask!.IsCompleted);

        await expirer.StopAsync(CancellationToken.None);

        // La cancelación del apagado sí sube: el host la reconoce y no la trata como un fallo.
        Assert.True(expirer.ExecuteTask.IsCompleted);
        Assert.True(expirer.ExecuteTask.IsCanceled);
    }

    private static MatchingEventsConsumer ConsumidorConUnidadDeTrabajoLenta()
    {
        var store = new InMemoryStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddSingleton<ITenantUnitOfWork>(new UnidadDeTrabajoConTiempoAgotado());
        services.AddSingleton<IServiceRequestRepository>(store);
        services.AddSingleton<IProcessedEventRepository>(store);
        services.AddSingleton<IOutbox>(store);
        services.AddScoped<MatchingStatusHandler>();
        var kafka = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = "localhost:1", GroupId = "pruebas" }).Build();
        return new MatchingEventsConsumer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kafka, NullLogger<MatchingEventsConsumer>.Instance);
    }

    private static string MensajeDeAsignacion() => JsonSerializer.Serialize(new
    {
        eventId = Guid.NewGuid(),
        eventType = "matching.technician-assigned",
        eventVersion = 1,
        occurredAt = Now,
        correlationId = Guid.NewGuid(),
        tenantId = Guid.NewGuid(),
        producer = "matching-service",
        data = new
        {
            serviceRequestId = Guid.NewGuid(),
            clientId = Guid.NewGuid(),
            attemptId = Guid.NewGuid(),
            technicianId = Guid.NewGuid(),
            assignedAt = Now,
        },
    });

    [Fact]
    public async Task Consumidor_ConTiempoAgotadoEnUnMensaje_DevuelveFalseParaReintentarlo()
    {
        using var consumidor = ConsumidorConUnidadDeTrabajoLenta();

        // Antes del arreglo la excepción subía hasta el bucle, que la tragaba en un catch vacío y dejaba de consumir en silencio.
        var ok = await consumidor.TryProcessAsync(MensajeDeAsignacion(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task Consumidor_ConElTokenCancelado_PropagaLaCancelacion()
    {
        using var consumidor = ConsumidorConUnidadDeTrabajoLenta();
        using var apagado = new CancellationTokenSource();
        await apagado.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumidor.TryProcessAsync(MensajeDeAsignacion(), apagado.Token));
    }
}