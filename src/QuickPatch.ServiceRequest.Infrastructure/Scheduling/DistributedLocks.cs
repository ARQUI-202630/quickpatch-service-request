using QuickPatch.ServiceRequest.Application.Abstractions;

using StackExchange.Redis;

namespace QuickPatch.ServiceRequest.Infrastructure.Scheduling;

/// <summary>
/// Candado en Redis (coordinación temporal de los procesos programados, DD 7.13 para RN-Q6; SAD 5.1). La clave lleva el
/// prefijo <c>service-request:</c>, el único que permite el usuario ACL del servicio. Se toma con <c>SET NX PX</c> y un
/// valor propio de la réplica, y se suelta solo si sigue siendo de ella: si una vuelta tarda más que el plazo, el
/// candado vence solo y otra réplica puede continuar.
/// </summary>
public sealed class RedisDistributedLock(IConnectionMultiplexer redis) : IDistributedLock
{
    public const string KeyPrefix = "service-request:lock:";

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var db = redis.GetDatabase();
        var key = new RedisKey(KeyPrefix + name);
        var token = new RedisValue(Guid.NewGuid().ToString("N"));
        return await db.LockTakeAsync(key, token, ttl) ? new Release(db, key, token) : null;
    }

    private sealed class Release(IDatabase db, RedisKey key, RedisValue token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await db.LockReleaseAsync(key, token);
    }
}

/// <summary>
/// Candado dentro del proceso, para desarrollo local y pruebas sin Redis (<c>Redis:Endpoint</c> vacío). Solo coordina una
/// réplica; en QA y producción el servicio usa <see cref="RedisDistributedLock"/>.
/// </summary>
public sealed class InProcessLock : IDistributedLock, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public void Dispose() => gate.Dispose();

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken) =>
        await gate.WaitAsync(TimeSpan.Zero, cancellationToken) ? new Release(gate) : null;

    private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}