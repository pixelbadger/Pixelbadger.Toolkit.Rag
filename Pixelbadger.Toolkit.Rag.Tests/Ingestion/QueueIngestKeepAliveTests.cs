using Azure.Core;
using Azure.Storage.Queues;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Testcontainers.Azurite;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

/// <summary>One Azurite container for the keep-alive tests (Docker required, like the SQL tests).</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public class QueueIngestKeepAliveTests(AzuriteFixture azurite) : IClassFixture<AzuriteFixture>
{
    private QueueClient NewQueue() => new(azurite.ConnectionString, "keepalive-" + Guid.NewGuid().ToString("N")[..12]);

    private static QueueIngestKeepAlive NewKeepAlive(QueueClient queue) => new(queue, NullLogger<QueueIngestKeepAlive>.Instance);

    private static async Task<int> CountAsync(QueueClient queue) =>
        (await queue.GetPropertiesAsync()).Value.ApproximateMessagesCount;

    [Fact]
    public async Task Busy_LeavesOneMarker_AndRepeatedBusyDoesNotAddMore()
    {
        var queue = NewQueue();
        using var keepAlive = NewKeepAlive(queue);

        await keepAlive.MarkBusyAsync(CancellationToken.None);
        await keepAlive.MarkBusyAsync(CancellationToken.None);

        (await CountAsync(queue)).Should().Be(1);
    }

    [Fact]
    public async Task Idle_RemovesTheMarker_AndBusyAfterIdleMarksAgain()
    {
        var queue = NewQueue();
        using var keepAlive = NewKeepAlive(queue);
        await keepAlive.MarkBusyAsync(CancellationToken.None);

        await keepAlive.MarkIdleAsync(CancellationToken.None);
        (await CountAsync(queue)).Should().Be(0);

        await keepAlive.MarkBusyAsync(CancellationToken.None);
        (await CountAsync(queue)).Should().Be(1);
    }

    [Fact]
    public async Task FirstIdle_ClearsMarkersLeftByAPreviousProcess()
    {
        var queue = NewQueue();
        await queue.CreateAsync();
        await queue.SendMessageAsync("busy");
        using var keepAlive = NewKeepAlive(queue);

        await keepAlive.MarkIdleAsync(CancellationToken.None);

        (await CountAsync(queue)).Should().Be(0);
    }

    [Fact]
    public async Task UnreachableQueue_IsLoggedNotThrown()
    {
        var options = new QueueClientOptions { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(2), Mode = RetryMode.Fixed } };
        var queue = new QueueClient(new Uri("http://127.0.0.1:1/devstoreaccount1/keepalive"), options);
        using var keepAlive = NewKeepAlive(queue);

        var act = async () =>
        {
            await keepAlive.MarkBusyAsync(CancellationToken.None);
            await keepAlive.MarkIdleAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }
}
