using FluentAssertions;
using Moq;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Messaging;

namespace Pixelbadger.Toolkit.Rag.Tests.Messaging;

/// <summary>Which job status changes each bus consumer forwards to its service (services are mocked).</summary>
public class ConsumerRoutingTests
{
    private static readonly Guid JobId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    [Fact]
    public async Task IngestJobConsumer_QueuedJob_ProcessesIt()
    {
        var service = new Mock<IIngestJobService>();
        using var cts = new CancellationTokenSource();

        await new IngestJobConsumer(service.Object).OnHandle(new JobStatusChanged(JobId, IngestJobStatus.Queued), cts.Token);

        service.Verify(s => s.ProcessAsync(JobId, cts.Token), Times.Once);
    }

    [Theory]
    [InlineData(IngestJobStatus.Processing)]
    [InlineData(IngestJobStatus.Succeeded)]
    [InlineData(IngestJobStatus.Skipped)]
    [InlineData(IngestJobStatus.Failed)]
    public async Task IngestJobConsumer_NonQueuedStatus_IsIgnored(IngestJobStatus status)
    {
        var service = new Mock<IIngestJobService>();

        await new IngestJobConsumer(service.Object).OnHandle(new JobStatusChanged(JobId, status), CancellationToken.None);

        service.Verify(s => s.ProcessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(IngestJobStatus.Succeeded)]
    [InlineData(IngestJobStatus.Skipped)]
    [InlineData(IngestJobStatus.Failed)]
    public async Task VectorIndexConsumer_FinishedJob_NotifiesService(IngestJobStatus status)
    {
        var service = new Mock<IVectorIndexService>();
        using var cts = new CancellationTokenSource();

        await new VectorIndexConsumer(service.Object).OnHandle(new JobStatusChanged(JobId, status), cts.Token);

        service.Verify(s => s.OnJobFinishedAsync(cts.Token), Times.Once);
    }

    [Theory]
    [InlineData(IngestJobStatus.Queued)]
    [InlineData(IngestJobStatus.Processing)]
    public async Task VectorIndexConsumer_UnfinishedJob_IsIgnored(IngestJobStatus status)
    {
        var service = new Mock<IVectorIndexService>();

        await new VectorIndexConsumer(service.Object).OnHandle(new JobStatusChanged(JobId, status), CancellationToken.None);

        service.Verify(s => s.OnJobFinishedAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
