using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Persistence;
using SlimMessageBus.Host;

namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>
/// Starts the bus consumers (and the outbox sender) once the database is migrated: registered after
/// <c>DatabaseMigrationHostedService</c>, and hosted services start in order. Host start is not held up: in the
/// background it waits (with backoff) until SQL accepts a connection, then starts the bus.
/// </summary>
/// <remarks>
/// The bus is resolved only after SQL answered: building it provisions its tables once and never retries, so it must
/// not be built while the database is unreachable (e.g. a paused serverless database still resuming). If starting
/// still fails, the application stops (the container is restarted) rather than run without consumers.
/// </remarks>
public sealed class MessageBusStartupService(
    IServiceProvider services,
    SqlStoreOptions sql,
    IHostApplicationLifetime lifetime,
    ILogger<MessageBusStartupService> logger) : IHostedService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _run;
    private IConsumerControl? _bus;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_run is not null)
            await _run;
        if (_bus is not null)
            await _bus.Stop();
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForDatabaseAsync(stoppingToken);
            logger.LogInformation("Starting message bus consumers");
            var bus = services.GetRequiredService<IConsumerControl>();
            _bus = bus;
            await bus.Start();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The message bus could not start; stopping the application");
            lifetime.StopApplication();
        }
    }

    private async Task WaitForDatabaseAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(sql.ConnectionString);
                await connection.OpenAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "SQL is not reachable yet; starting the message bus in {Delay}", delay);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }
}
