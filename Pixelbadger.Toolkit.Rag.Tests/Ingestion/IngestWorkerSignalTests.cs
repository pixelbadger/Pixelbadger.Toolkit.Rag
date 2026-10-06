using System.Diagnostics;
using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Ingestion;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

public class IngestWorkerSignalTests
{
    [Fact]
    public async Task Wait_ReturnsImmediately_WhenNotifiedBeforehand_AndConsumesTheNotification()
    {
        var signal = new IngestWorkerSignal();
        signal.Notify();
        signal.Notify(); // coalesces

        var sw = Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        sw.Restart();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40), "only one notification was pending");
    }

    [Fact]
    public async Task Wait_WakesUp_WhenNotifiedWhileWaiting()
    {
        var signal = new IngestWorkerSignal();
        var wait = signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        signal.Notify();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Wait_TimesOutQuietly()
    {
        var signal = new IngestWorkerSignal();

        var act = async () => await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Wait_PropagatesCallerCancellation()
    {
        var signal = new IngestWorkerSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var act = async () => await signal.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
