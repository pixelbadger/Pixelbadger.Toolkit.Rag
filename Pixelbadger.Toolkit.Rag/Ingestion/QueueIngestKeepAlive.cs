using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Keeps a marker message in an Azure Storage queue while ingest has work. A Container Apps scale rule on
/// that queue's length keeps the (otherwise scale-to-zero) replica running until ingest reports idle, so a long
/// ingest is not cut off when HTTP traffic stops.
/// <list type="bullet">
/// <item>Busy: one visible marker with a time-to-live, replaced every <see cref="RefreshAfter"/> while busy (the job
/// heartbeat calls <see cref="MarkBusyAsync"/> too), so a process that dies holding it keeps the host alive for at
/// most <see cref="MarkerTimeToLive"/>.</item>
/// <item>Idle: the queue is cleared, which also removes markers left by a previous process. Single-instance
/// assumption: only this process writes the queue.</item>
/// </list>
/// Nothing ever receives the messages; they only exist to be counted.
/// </summary>
public sealed class QueueIngestKeepAlive(QueueClient queue, ILogger<QueueIngestKeepAlive> logger) : IIngestKeepAlive, IDisposable
{
    /// <summary>The connection (and Aspire resource) name of the queue.</summary>
    public const string ConnectionName = "ingest-active";

    public static readonly TimeSpan MarkerTimeToLive = TimeSpan.FromHours(2);

    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(30);

    // The job heartbeat and the job/idle paths can all call in.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SendReceipt? _marker;
    private DateTimeOffset _markedAt;
    private bool _queueExists;
    // False at startup: a previous process may have left a marker behind.
    private bool _clean;
    private bool _failing;

    public async Task MarkBusyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_marker is not null && DateTimeOffset.UtcNow - _markedAt < RefreshAfter)
                return;

            await RunAsync("mark the ingest worker busy", async ct =>
            {
                await EnsureQueueAsync(ct);
                var previous = _marker;
                _marker = (await queue.SendMessageAsync("busy", visibilityTimeout: null, timeToLive: MarkerTimeToLive, ct)).Value;
                _markedAt = DateTimeOffset.UtcNow;
                _clean = false;
                if (previous is not null)
                    await queue.DeleteMessageAsync(previous.MessageId, previous.PopReceipt, ct);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkIdleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_clean)
                return;

            await RunAsync("mark the ingest worker idle", async ct =>
            {
                await EnsureQueueAsync(ct);
                await queue.ClearMessagesAsync(ct);
                _marker = null;
                _clean = true;
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task EnsureQueueAsync(CancellationToken cancellationToken)
    {
        if (_queueExists)
            return;
        await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        _queueExists = true;
    }

    private async Task RunAsync(string action, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken);
            if (_failing)
                logger.LogInformation("The ingest keep-alive queue is reachable again");
            _failing = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged once per outage: the next busy or idle call retries.
            if (!_failing)
                logger.LogWarning(ex, "Could not {Action} in the keep-alive queue; the host may scale down while jobs remain (they resume on the next start)", action);
            _failing = true;
        }
    }
}
