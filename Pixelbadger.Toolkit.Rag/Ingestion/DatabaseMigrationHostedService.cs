using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Applies EF Core migrations at startup (when <see cref="RagOptions.ApplyMigrationsOnStartup"/> is set).
/// Registered before <see cref="InFlightJobRecoveryHostedService"/> and <see cref="IngestWorker"/>: hosted services start in order, so the worker never polls a
/// missing table. A failure here stops the host from starting.
/// </summary>
public sealed class DatabaseMigrationHostedService(
    IServiceScopeFactory scopes,
    RagOptions options,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.ApplyMigrationsOnStartup)
        {
            logger.LogInformation("Skipping database migrations (Rag:ApplyMigrationsOnStartup is false)");
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        logger.LogInformation("Applying database migrations");
        await scope.ServiceProvider.GetRequiredService<IDocumentStore>().MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
