using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Quotes;
using QuickPatch.ServiceRequest.Infrastructure.Options;

namespace QuickPatch.ServiceRequest.Infrastructure.Scheduling;

/// <summary>
/// Proceso programado del vencimiento de cotizaciones (RN-Q6, DD 7.13). Cada <c>Quotes:ExpiryInterval</c> toma el candado de
/// Redis para que una sola réplica recorra las cotizaciones vencidas, y vence cada una en su tenant
/// (<see cref="ExpireQuotesBatch"/>). Si Redis o la base fallan, registra el error y lo intenta en la siguiente vuelta: el
/// vencimiento es idempotente y no se pierde.
/// </summary>
public sealed partial class QuoteExpirer(
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    IOptions<QuotesOptions> options,
    TimeProvider clock,
    ILogger<QuoteExpirer> logger) : BackgroundService
{
    public const string LockName = "quote-expirer";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.Value.ExpiryInterval, clock, stoppingToken);
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogExpiryFailed(logger, ex);
            }
        }
    }

    /// <summary>Una vuelta; devuelve las cotizaciones vencidas, o null si otra réplica tenía el candado.</summary>
    public async Task<int?> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        // El candado dura dos intervalos: alcanza para una vuelta y, si la réplica cae, se libera solo.
        await using var held = await distributedLock.TryAcquireAsync(LockName, settings.ExpiryInterval * 2, cancellationToken);
        if (held is null)
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var expired = await scope.ServiceProvider.GetRequiredService<ExpireQuotesBatch>()
            .RunAsync(settings.ExpiryBatchSize, cancellationToken);
        if (expired > 0)
        {
            LogExpired(logger, expired);
        }

        return expired;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cotizaciones vencidas y solicitudes canceladas: {Count} (RN-Q6).")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la vuelta del vencimiento de cotizaciones; se reintenta en la siguiente.")]
    private static partial void LogExpiryFailed(ILogger logger, Exception exception);
}