using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Messaging;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Support;

/// <summary>
/// The real bus (SlimMessageBus SQL transport + outbox) on a test database, with the rest of the container supplied
/// by the test (<paramref name="configure"/>). Start it with <see cref="StartAsync"/> to run the consumers; leave it
/// stopped to only publish (events then stay in the outbox / transport tables).
/// </summary>
public sealed class BusHarness : IAsyncDisposable
{
    private readonly MessageBusStartupService _startup;

    private BusHarness(ServiceProvider services, RagOptions options)
    {
        Services = services;
        Options = options;
        _startup = ActivatorUtilities.CreateInstance<MessageBusStartupService>(services);
    }

    public ServiceProvider Services { get; }

    public RagOptions Options { get; }

    public IServiceScopeFactory Scopes => Services.GetRequiredService<IServiceScopeFactory>();

    /// <summary>Migrates <paramref name="connectionString"/> and builds a container with the bus.</summary>
    public static async Task<BusHarness> CreateAsync(string connectionString, Action<IServiceCollection, RagOptions>? configure = null, Action<RagOptions>? options = null)
    {
        var rag = new RagOptions { Sql = new SqlStoreOptions { ConnectionString = connectionString } };
        rag.Ingest.PollIntervalSeconds = 1;
        options?.Invoke(rag);
        await new SqlDocumentStore(rag.Sql).MigrateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, TestLifetime>();
        services.AddSingleton(rag);
        services.AddSingleton(rag.Sql);
        services.AddSingleton(rag.Ingest);
        services.AddRagMessaging(rag);
        configure?.Invoke(services, rag);
        return new BusHarness(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), rag);
    }

    /// <summary>Starts the consumers and waits until they run.</summary>
    public async Task StartAsync()
    {
        await _startup.StartAsync(CancellationToken.None);
        var bus = (SlimMessageBus.Host.IConsumerControl)Services.GetRequiredService<SlimMessageBus.Host.IConsumerControl>();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!bus.IsStarted && DateTime.UtcNow < until)
            await Task.Delay(50);
        if (!bus.IsStarted)
            throw new TimeoutException("The bus did not start.");
    }

    public async ValueTask DisposeAsync()
    {
        await _startup.StopAsync(CancellationToken.None);
        await Services.DisposeAsync();
    }
}

/// <summary>The outbox clean-up task needs an application lifetime (a host supplies one).</summary>
public sealed class TestLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}

/// <summary>Records the events each subscription received.</summary>
public sealed class RecordingJobServices : IIngestJobService, IVectorIndexService
{
    public ConcurrentQueue<Guid> Ingested { get; } = new();

    public int IndexCalls;

    public Func<Guid, CancellationToken, Task>? OnIngest { get; set; }

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (OnIngest is not null)
            await OnIngest(jobId, cancellationToken);
        Ingested.Enqueue(jobId);
    }

    public Task OnJobFinishedAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref IndexCalls);
        return Task.CompletedTask;
    }

    public static void Register(IServiceCollection services, RecordingJobServices recorder)
    {
        services.AddSingleton<IIngestJobService>(recorder);
        services.AddSingleton<IVectorIndexService>(recorder);
    }
}

public static class Eventually
{
    public static async Task<bool> TrueAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }
        return condition();
    }
}
