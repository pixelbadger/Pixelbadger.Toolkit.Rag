using System.Text;
using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Messaging;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

public class InMemoryIngestQueueTests
{
    private const int MaxChunk = 200;

    private static InMemoryIngestQueue NewQueue() => new();

    private static IngestUpload Upload(string logicalPath, string content = "hello") =>
        new(logicalPath, Encoding.UTF8.GetByteCount(content), () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static async Task<EnqueuedDocument> EnqueueOne(InMemoryIngestQueue queue, string logicalPath = "a.txt") =>
        (await queue.EnqueueNewDocumentsAsync([Upload(logicalPath)], MaxChunk)).Single();

    [Fact]
    public async Task Enqueue_PublishesQueuedForEachJob_InUploadOrder()
    {
        var queue = NewQueue();

        var created = await queue.EnqueueNewDocumentsAsync([Upload("a.txt"), Upload("b.md")], MaxChunk);

        queue.Events.Should().Equal(
            new JobStatusChanged(created[0].JobId, IngestJobStatus.Queued),
            new JobStatusChanged(created[1].JobId, IngestJobStatus.Queued));
    }

    [Fact]
    public async Task Enqueue_FailingPartWay_StoresNothingAndPublishesNothing()
    {
        var queue = NewQueue();
        queue.FailEnqueueAfter = 1;

        var act = () => queue.EnqueueNewDocumentsAsync([Upload("a.txt"), Upload("b.md")], MaxChunk);

        await act.Should().ThrowAsync<IOException>();
        queue.Events.Should().BeEmpty();
        (await queue.GetJobsAsync(1, 10)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Reingest_WhenIdle_CreatesJobAndPublishesQueued()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        await queue.BeginProcessingAsync(created.JobId);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));
        var before = queue.Events.Count;

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "again"), MaxChunk);

        result.Outcome.Should().Be(ReingestOutcome.Created);
        queue.Events.Skip(before).Should().Equal(new JobStatusChanged(result.JobId!.Value, IngestJobStatus.Queued));
    }

    [Fact]
    public async Task Reingest_ReplacingQueuedJob_PublishesNothing()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        var before = queue.Events.Count;

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "again"), MaxChunk);

        result.Outcome.Should().Be(ReingestOutcome.ReplacedQueued);
        queue.Events.Should().HaveCount(before);
    }

    [Fact]
    public async Task Reingest_WhileProcessing_IsConflictAndPublishesNothing()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        await queue.BeginProcessingAsync(created.JobId);
        var before = queue.Events.Count;

        var result = await queue.EnqueueReingestAsync(created.DocumentId, Upload("a.txt", "again"), MaxChunk);

        result.Outcome.Should().Be(ReingestOutcome.Conflict);
        queue.Events.Should().HaveCount(before);
    }

    [Fact]
    public async Task Reingest_UnknownDocument_IsNotFoundAndPublishesNothing()
    {
        var queue = NewQueue();

        var result = await queue.EnqueueReingestAsync(Guid.NewGuid(), Upload("a.txt"), MaxChunk);

        result.Outcome.Should().Be(ReingestOutcome.NotFound);
        queue.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task BeginProcessing_QueuedJob_BecomesProcessingWithClaimDetails()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue, "docs/a.txt");

        var claim = await queue.BeginProcessingAsync(created.JobId);

        claim.Should().Be(new IngestJobClaim(created.JobId, created.DocumentId, 1, MaxChunk, "docs/a.txt", 5));
        var document = await queue.GetDocumentAsync(created.DocumentId);
        document!.IndexStatus.Should().Be(IndexStatus.Processing);
        document.LatestJob!.Status.Should().Be(IngestJobStatus.Processing);
        document.LatestJob.Attempts.Should().Be(1);
        document.LatestJob.StartedAtUtc.Should().NotBeNull();
        queue.Events.Last().Should().Be(new JobStatusChanged(created.JobId, IngestJobStatus.Processing));
    }

    [Fact]
    public async Task BeginProcessing_ProcessingJob_IncrementsAttemptsAndKeepsStartedTime()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        await queue.BeginProcessingAsync(created.JobId);
        var startedFirst = (await queue.GetDocumentAsync(created.DocumentId))!.LatestJob!.StartedAtUtc;

        var second = await queue.BeginProcessingAsync(created.JobId);

        second!.Attempts.Should().Be(2);
        var job = (await queue.GetDocumentAsync(created.DocumentId))!.LatestJob!;
        job.Attempts.Should().Be(2);
        job.StartedAtUtc.Should().Be(startedFirst);
        queue.Events.Count(e => e.JobId == created.JobId && e.Status == IngestJobStatus.Processing).Should().Be(2);
    }

    [Fact]
    public async Task BeginProcessing_TerminalJob_ReturnsNullAndPublishesNothing()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Skipped, 0));
        var before = queue.Events.Count;

        var claim = await queue.BeginProcessingAsync(created.JobId);

        claim.Should().BeNull();
        queue.Events.Should().HaveCount(before);
        (await queue.GetDocumentAsync(created.DocumentId))!.LatestJob!.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task BeginProcessing_UnknownJob_ReturnsNullAndPublishesNothing()
    {
        var queue = NewQueue();

        var claim = await queue.BeginProcessingAsync(Guid.NewGuid());

        claim.Should().BeNull();
        queue.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_PublishesTerminalStatus()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        await queue.BeginProcessingAsync(created.JobId);

        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Failed, Error: "bad file"));

        queue.Events.Last().Should().Be(new JobStatusChanged(created.JobId, IngestJobStatus.Failed));
        (await queue.GetDocumentAsync(created.DocumentId))!.IndexStatus.Should().Be(IndexStatus.Failed);
    }

    [Fact]
    public async Task Complete_ForRemovedJob_IsQuietAndPublishesNothing()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        await queue.BeginProcessingAsync(created.JobId);
        queue.RemoveDocument(created.DocumentId);
        var before = queue.Events.Count;

        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 1));

        queue.Events.Should().HaveCount(before);
    }

    [Fact]
    public async Task HasActiveJobs_TrueWhileQueuedOrProcessing_FalseOnceTerminal()
    {
        var queue = NewQueue();
        (await queue.HasActiveJobsAsync()).Should().BeFalse();

        var created = await EnqueueOne(queue);
        (await queue.HasActiveJobsAsync()).Should().BeTrue("queued");

        await queue.BeginProcessingAsync(created.JobId);
        (await queue.HasActiveJobsAsync()).Should().BeTrue("processing");

        await queue.CompleteAsync(created.JobId, new IngestJobOutcome(IngestJobStatus.Succeeded, 3));
        (await queue.HasActiveJobsAsync()).Should().BeFalse("terminal");
    }

    [Fact]
    public async Task RemoveDocument_DeletesDocumentAndItsJobs()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);

        queue.RemoveDocument(created.DocumentId).Should().BeTrue();

        (await queue.GetDocumentAsync(created.DocumentId)).Should().BeNull();
        (await queue.GetJobsAsync(1, 10)).TotalCount.Should().Be(0);
        (await queue.HasActiveJobsAsync()).Should().BeFalse();
        queue.RemoveDocument(created.DocumentId).Should().BeFalse("already removed");
    }

    [Fact]
    public async Task RemoveDocument_PublishesNoEvent()
    {
        var queue = NewQueue();
        var created = await EnqueueOne(queue);
        var before = queue.Events.Count;

        queue.RemoveDocument(created.DocumentId);

        queue.Events.Should().HaveCount(before);
    }
}
