using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

/// <summary>VectorIndexService against mocked queue, store and keep-alive: only the last finished job builds the index.</summary>
public class VectorIndexServiceTests
{
    private readonly Mock<IIngestQueue> _queue = new();
    private readonly Mock<IDocumentStore> _store = new();
    private readonly Mock<IIngestKeepAlive> _keepAlive = new();

    private VectorIndexService NewService() =>
        new(_queue.Object, _store.Object, _keepAlive.Object, NullLogger<VectorIndexService>.Instance);

    [Fact]
    public async Task OnJobFinished_WithActiveJobs_DoesNothing()
    {
        _queue.Setup(q => q.HasActiveJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await NewService().OnJobFinishedAsync(CancellationToken.None);

        _store.Verify(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>()), Times.Never);
        _keepAlive.Verify(k => k.MarkIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnJobFinished_WhenIdle_EnsuresVectorIndexThenMarksIdle()
    {
        _queue.Setup(q => q.HasActiveJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sequence = new MockSequence();
        _store.InSequence(sequence).Setup(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _keepAlive.InSequence(sequence).Setup(k => k.MarkIdleAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await NewService().OnJobFinishedAsync(CancellationToken.None);

        _store.Verify(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>()), Times.Once);
        _keepAlive.Verify(k => k.MarkIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
        _store.VerifyNoOtherCalls();
        _keepAlive.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OnJobFinished_WhenIndexCheckThrows_PropagatesAndDoesNotMarkIdle()
    {
        _queue.Setup(q => q.HasActiveJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _store.Setup(s => s.EnsureVectorIndexAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sql down"));

        var act = () => NewService().OnJobFinishedAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("sql down");
        _keepAlive.Verify(k => k.MarkIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnJobFinished_PassesCancellationTokenToQueueStoreAndKeepAlive()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _queue.Setup(q => q.HasActiveJobsAsync(token)).ReturnsAsync(false);
        _store.Setup(s => s.EnsureVectorIndexAsync(token)).Returns(Task.CompletedTask);
        _keepAlive.Setup(k => k.MarkIdleAsync(token)).Returns(Task.CompletedTask);

        await NewService().OnJobFinishedAsync(token);

        _queue.Verify(q => q.HasActiveJobsAsync(token), Times.Once);
        _store.Verify(s => s.EnsureVectorIndexAsync(token), Times.Once);
        _keepAlive.Verify(k => k.MarkIdleAsync(token), Times.Once);
    }
}
