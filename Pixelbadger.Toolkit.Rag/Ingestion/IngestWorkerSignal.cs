using System.Threading.Channels;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// In-process nudge from the ingest endpoint to the worker so a new job is picked up immediately instead of
/// on the next poll. Notifications coalesce: any number of <see cref="Notify"/> calls wake one wait.
/// </summary>
public sealed class IngestWorkerSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Completes when notified or after <paramref name="timeout"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out: poll again.
        }
    }
}
