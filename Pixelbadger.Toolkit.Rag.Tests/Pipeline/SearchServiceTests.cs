using FluentAssertions;
using Moq;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>SearchService against a mocked <see cref="IDocumentStore"/>: ordering, scoring, hydration and pass-through.</summary>
public class SearchServiceTests
{
    private static readonly Guid DocText = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DocImage = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid DocAudio = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static ChunkRecord Chunk(int id, Guid doc, string path, Modality modality, string? text = null, long? start = null, long? end = null) =>
        new(id, Guid.NewGuid(), id, doc, path, Path.GetFileName(path), 1, modality, start, end, text);

    private static SearchService NewService(Mock<IDocumentStore> store) => new(store.Object, new MockEmbeddingService());

    [Fact]
    public async Task Search_ReturnsResultsInVectorHitOrder_WithScoreAsCosineSimilarity()
    {
        var store = new Mock<IDocumentStore>();
        store.Setup(s => s.SearchAsync(It.IsAny<float[]>(), 3, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new VectorHit(3, 0.10f), new VectorHit(1, 0.25f), new VectorHit(2, 0.75f)]);
        // Hydration order is unspecified by the store contract: return it scrambled.
        store.Setup(s => s.GetChunksAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Chunk(2, DocText, "b.txt", Modality.Text, "b"), Chunk(3, DocText, "c.txt", Modality.Text, "c"), Chunk(1, DocText, "a.txt", Modality.Text, "a")]);

        var results = await NewService(store).SearchAsync("q", 3);

        results.Select(r => r.Content).Should().Equal("c", "a", "b");
        results.Select(r => r.Score).Should().Equal(0.90f, 0.75f, 0.25f);
    }

    [Fact]
    public async Task Search_AsksTheStoreForExactlyMaxResults()
    {
        var store = new Mock<IDocumentStore>();
        store.Setup(s => s.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var results = await NewService(store).SearchAsync("q", 7);

        results.Should().BeEmpty();
        store.Verify(s => s.SearchAsync(It.IsAny<float[]>(), 7, null, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.GetChunksAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_SkipsHitsWhoseChunkIsMissingFromSql()
    {
        var store = new Mock<IDocumentStore>();
        store.Setup(s => s.SearchAsync(It.IsAny<float[]>(), 5, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new VectorHit(1, 0.1f), new VectorHit(2, 0.2f), new VectorHit(3, 0.3f)]);
        store.Setup(s => s.GetChunksAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Chunk(1, DocText, "a.txt", Modality.Text, "a"), Chunk(3, DocText, "c.txt", Modality.Text, "c")]);

        var results = await NewService(store).SearchAsync("q", 5);

        results.Select(r => r.Content).Should().Equal("a", "c");
    }

    [Fact]
    public async Task Search_ReturnsImageAndAudioChunksAlongsideText_RankedOnlyByVectorDistance()
    {
        var store = new Mock<IDocumentStore>();
        store.Setup(s => s.SearchAsync(It.IsAny<float[]>(), 5, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new VectorHit(2, 0.05f), new VectorHit(1, 0.20f), new VectorHit(3, 0.40f)]);
        store.Setup(s => s.GetChunksAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Chunk(1, DocText, "docs/a.md", Modality.Text, "text", 0, 4),
                Chunk(2, DocImage, "img/photo.png", Modality.Image),
                Chunk(3, DocAudio, "audio/talk.mp3", Modality.Audio, null, 0, 30_000)]);

        var results = await NewService(store).SearchAsync("q", 5);

        results.Select(r => r.Modality).Should().Equal(Modality.Image, Modality.Text, Modality.Audio);
        results[0].Content.Should().BeNull();
        results[0].SourceFile.Should().Be("photo.png");
        results[0].SourcePath.Should().Be("img/photo.png");
        results[0].Score.Should().BeApproximately(0.95f, 1e-6f);
        results[2].LocatorEnd.Should().Be(30_000);
        results[2].DocumentId.Should().Be(DocAudio);
    }

    [Fact]
    public async Task Search_PassesTheDocumentIdFilterThrough()
    {
        var store = new Mock<IDocumentStore>();
        store.Setup(s => s.SearchAsync(It.IsAny<float[]>(), 4, It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var filter = new List<Guid> { DocText, DocImage };

        await NewService(store).SearchAsync("q", 4, filter);

        store.Verify(s => s.SearchAsync(It.IsAny<float[]>(), 4, It.Is<IReadOnlyCollection<Guid>?>(x => x!.SequenceEqual(filter)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_RejectsBlankQuery(string query)
    {
        var act = async () => await NewService(new Mock<IDocumentStore>()).SearchAsync(query, 5);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Search_RejectsInvalidMaxResults(int maxResults)
    {
        var act = async () => await NewService(new Mock<IDocumentStore>()).SearchAsync("q", maxResults);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Search_RejectsOverlongQuery_AndTooManyDocumentIds()
    {
        var service = NewService(new Mock<IDocumentStore>());

        var tooLong = async () => await service.SearchAsync(new string('a', SearchService.MaxQueryLength + 1), 5);
        var tooMany = async () => await service.SearchAsync("q", 5, Enumerable.Range(0, SearchService.MaxDocumentIds + 1).Select(_ => Guid.NewGuid()).ToList());

        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooMany.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
