using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

public class DocumentServiceTests
{
    private readonly Mock<IDocumentStore> _store = new();
    private readonly Mock<IIngestQueue> _queue = new();
    private readonly Mock<IIngestKeepAlive> _keepAlive = new();

    private DocumentService NewService() => new(
        new IngestSettings(), _store.Object, new IngestJobRegistry(), _queue.Object, _keepAlive.Object,
        NullLogger<DocumentService>.Instance);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Delete_MarksTheKeepAliveIdle_OnlyWhenNoJobRemains(bool activeJobsLeft, int expectedIdleCalls)
    {
        var id = Guid.NewGuid();
        _store.Setup(s => s.DeleteDocumentAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _queue.Setup(q => q.HasActiveJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(activeJobsLeft);

        (await NewService().DeleteAsync(id)).Should().Be(DeleteOutcome.Deleted);

        _keepAlive.Verify(k => k.MarkIdleAsync(It.IsAny<CancellationToken>()), Times.Exactly(expectedIdleCalls));
    }

    [Fact]
    public async Task Delete_OfAnUnknownDocument_DoesNotTouchTheKeepAlive()
    {
        _store.Setup(s => s.DeleteDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await NewService().DeleteAsync(Guid.NewGuid())).Should().Be(DeleteOutcome.NotFound);

        _keepAlive.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Delete_StillSucceeds_WhenTheIdleCheckFails()
    {
        _store.Setup(s => s.DeleteDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _queue.Setup(q => q.HasActiveJobsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("sql down"));

        (await NewService().DeleteAsync(Guid.NewGuid())).Should().Be(DeleteOutcome.Deleted);
    }
}
