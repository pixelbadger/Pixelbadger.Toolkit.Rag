using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>
/// End-to-end ingest + vector search behaviour against the <see cref="IDocumentStore"/> contract.
/// Subclasses choose the store: in-memory (unit) or real SQL Server (integration).
/// </summary>
public abstract class PipelineBehaviorTests : IDisposable
{
    private readonly List<PipelineHarness> _harnesses = new();

    /// <summary>The store under test and how to create the (Queued) document an ingest writes into.</summary>
    protected sealed record StoreUnderTest(IDocumentStore Store, Func<string, Task<Guid>> CreateDocument);

    protected abstract Task<StoreUnderTest> CreateStoreAsync();

    protected async Task<PipelineHarness> NewHarnessAsync()
    {
        var (store, createDocument) = await CreateStoreAsync();
        var harness = new PipelineHarness(store, createDocument);
        _harnesses.Add(harness);
        return harness;
    }

    public void Dispose()
    {
        foreach (var h in _harnesses) h.Dispose();
    }

    private static async Task<IReadOnlyList<ChunkRecord>> ChunksOfAsync(PipelineHarness h, params int[] ids)
        => await h.Store.GetChunksAsync(ids);

    // ---- ingestion ----

    [SkippableFact]
    public async Task IngestFile_Text_StoresChunksWithLocatorsTitleAndHash()
    {
        using var h = await NewHarnessAsync();
        const string content = "Alpha paragraph about apples.\n\nBeta paragraph about bananas.";
        var path = h.Write("fruit.txt", content);

        var result = await h.IngestAsync(path);

        result.Modality.Should().Be(Modality.Text);
        result.ChunkCount.Should().Be(2);
        result.DocumentId.Should().NotBeEmpty();
        result.FilePath.Should().Be("fruit.txt");

        var results = await h.Search.SearchAsync("bananas", 5);
        var banana = results.Single(r => r.Content!.Contains("bananas"));
        banana.Ordinal.Should().Be(2);
        banana.DocumentId.Should().Be(result.DocumentId);
        banana.SourceFile.Should().Be("fruit.txt");
        banana.SourcePath.Should().Be("fruit.txt");
        content.Substring((int)banana.LocatorStart!.Value, (int)(banana.LocatorEnd!.Value - banana.LocatorStart.Value))
            .Should().Be(banana.Content);
    }

    [SkippableFact]
    public async Task Reingest_ReplacesPreviousContent()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("doc.txt", "zebra stripes\n\nzebra herd");
        var first = await h.IngestAsync(path);
        (await h.Search.SearchAsync("zebra", 10)).Should().HaveCount(2);

        File.WriteAllText(path, "giraffe neck");
        var second = await h.IngestAsync(path, documentId: first.DocumentId);

        second.DocumentId.Should().Be(first.DocumentId);
        second.ChunkCount.Should().Be(1);
        var results = await h.Search.SearchAsync("zebra", 10);
        results.Should().OnlyContain(r => !r.Content!.Contains("zebra"));
        results.Should().OnlyContain(r => r.Content == "giraffe neck");
        results.Should().HaveCount(1);
    }

    [SkippableFact]
    public async Task Ingest_AssignsUniqueChunkIds()
    {
        using var h = await NewHarnessAsync();
        h.Write("a.txt", "one\n\ntwo\n\nthree");
        h.Write("b.md", "# H1\nx\n\n# H2\ny");

        await h.IngestAllAsync();

        var results = await h.Search.SearchAsync("one two three x y H1 H2", 50);
        results.Select(r => r.ChunkId).Should().OnlyHaveUniqueItems();
        results.Should().NotBeEmpty();
    }

    [SkippableFact]
    public async Task IngestFile_Image_IsOneChunkWithoutText()
    {
        using var h = await NewHarnessAsync();
        var path = h.WriteBytes("photo.png", [1, 2, 3, 4, 5]);

        var result = await h.IngestAsync(path);

        result.Modality.Should().Be(Modality.Image);
        result.ChunkCount.Should().Be(1);

        // A media-only corpus is searchable.
        var hits = await h.Search.SearchAsync("a photo", 5);
        var hit = hits.Single();
        hit.Modality.Should().Be(Modality.Image);
        hit.Content.Should().BeNull();
        hit.LocatorStart.Should().BeNull();
    }

    [SkippableFact]
    public async Task IngestFile_Audio_CreatesOneChunkPerWindow_WithMillisecondLocators()
    {
        using var h = await NewHarnessAsync();
        h.Audio.WindowCount = 3;
        var path = h.WriteBytes("talk.wav", [9, 9, 9]);

        var result = await h.IngestAsync(path);

        result.Modality.Should().Be(Modality.Audio);
        result.ChunkCount.Should().Be(3);
        var hits = await h.Search.SearchAsync("spoken words", 10);
        hits.Should().HaveCount(3);
        hits.OrderBy(r => r.Ordinal).Select(r => (r.LocatorStart, r.LocatorEnd))
            .Should().Equal((0L, 30_000L), (30_000L, 60_000L), (60_000L, 90_000L));
        hits.Should().OnlyContain(r => r.Modality == Modality.Audio && r.Content == null);
    }

    [SkippableFact]
    public async Task Ingest_RoutesByExtension_AndKeepsLogicalPaths()
    {
        using var h = await NewHarnessAsync();
        h.Write("notes.txt", "plain notes");
        h.Write("sub/readme.md", "# Title\nmarkdown body");
        h.WriteBytes("sub/deep/pic.jpg", [1, 2, 3]);
        h.WriteBytes("clip.mp3", [4, 5, 6]);

        var results = await h.IngestAllAsync();

        results.Select(r => r.FilePath).Should().BeEquivalentTo("notes.txt", "sub/readme.md", "sub/deep/pic.jpg", "clip.mp3");
        results.Select(r => r.Modality).Order().Should().Equal(Modality.Text, Modality.Text, Modality.Image, Modality.Audio);
    }

    [SkippableFact]
    public async Task Ingest_EmptyContent_YieldsNoChunks()
    {
        using var h = await NewHarnessAsync();

        var result = await h.IngestAsync(h.Write("empty.txt", "   \n\n  "));

        result.ChunkCount.Should().Be(0);
    }

    [SkippableFact]
    public async Task Ingest_SameDocumentId_ReplacesRatherThanDuplicates_RegardlessOfLocalPath()
    {
        using var h = await NewHarnessAsync();
        var first = h.Write("tmp-one/upload.txt", "zebra stripes");
        var second = h.Write("tmp-two/upload.txt", "giraffe neck");
        var documentId = await h.NewDocumentAsync("docs/animals.txt");

        var a = await h.Ingester.IngestAsync(new IngestSource(first, "docs/animals.txt", documentId));
        var b = await h.Ingester.IngestAsync(new IngestSource(second, "docs/animals.txt", documentId));

        b.DocumentId.Should().Be(a.DocumentId).And.Be(documentId);
        var all = await h.Search.SearchAsync("zebra giraffe", 10);
        all.Should().ContainSingle().Which.Content.Should().Be("giraffe neck");
        all[0].DocumentId.Should().Be(documentId);
        all[0].SourcePath.Should().Be("docs/animals.txt");
        all[0].SourceFile.Should().Be("animals.txt");
    }

    [SkippableFact]
    public async Task Ingest_SamePath_InTwoDocuments_AreDifferentDocuments()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("same.txt", "shared body");

        var a = await h.IngestAsync(path);
        var b = await h.IngestAsync(path);

        b.DocumentId.Should().NotBe(a.DocumentId);
        (await h.Search.SearchAsync("shared body", 10)).Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task Ingest_NormalisesLogicalPathSeparators()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("x.txt", "content");
        var documentId = await h.NewDocumentAsync("docs/x.txt");

        var result = await h.Ingester.IngestAsync(new IngestSource(path, "\\docs\\x.txt", documentId));

        result.FilePath.Should().Be("docs/x.txt");
        (await h.Search.SearchAsync("content", 5)).Single().SourcePath.Should().Be("docs/x.txt");
    }

    [SkippableFact]
    public async Task Ingest_IntoAMissingDocument_Throws_AndWritesNothing()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("ghost.txt", "ghost words");
        var unknown = Guid.NewGuid();

        var act = async () => await h.Ingester.IngestAsync(new IngestSource(path, "ghost.txt", unknown));

        await act.Should().ThrowAsync<DocumentNotFoundException>().Where(e => e.DocumentId == unknown);
        (await h.Search.SearchAsync("ghost", 5)).Should().BeEmpty();
    }

    // ---- delete ----

    [SkippableFact]
    public async Task DeleteDocument_RemovesItsChunks_AndLeavesOthers()
    {
        using var h = await NewHarnessAsync();
        h.Write("keep.txt", "shared phrase keep");
        h.Write("drop.txt", "shared phrase drop");
        var results = await h.IngestAllAsync();
        var drop = results.Single(r => r.FilePath == "drop.txt");
        (await h.Search.SearchAsync("shared phrase", 10)).Should().HaveCount(2);

        var outcome = await h.NewDocumentService().DeleteAsync(drop.DocumentId);

        outcome.Should().Be(DeleteOutcome.Deleted);
        var left = await h.Search.SearchAsync("shared phrase", 10);
        left.Should().ContainSingle().Which.SourcePath.Should().Be("keep.txt");
        (await h.NewDocumentService().DeleteAsync(drop.DocumentId)).Should().Be(DeleteOutcome.NotFound);
    }

    // ---- ingestion safety ----

    [SkippableFact]
    public async Task IngestFile_Throws_WhenFileMissing()
    {
        using var h = await NewHarnessAsync();

        var act = async () => await h.IngestAsync(Path.Combine(h.ContentDir, "missing.txt"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [SkippableFact]
    public async Task IngestFile_RejectsFilesOverSizeLimit()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("big.txt", new string('a', 2048));

        var act = async () => await h.IngestAsync(path, new IngestOptions { MaxFileSizeBytes = 1024 });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeding the limit*");
    }

    [SkippableFact]
    public async Task IngestFile_RejectsChunksOverLimit()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("long.txt", new string('a', 500));

        var act = async () => await h.IngestAsync(path, new IngestOptions { MaxChunkCharacters = 100 });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*chunk*");
    }

    [SkippableTheory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task IngestFile_RejectsInvalidOptions(long maxBytes, int maxChunk)
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("a.txt", "x");

        var act = async () => await h.IngestAsync(path, new IngestOptions { MaxFileSizeBytes = maxBytes, MaxChunkCharacters = maxChunk });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [SkippableFact]
    public async Task IngestFile_Throws_ForUnsupportedExtension()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("doc.pdf", "x");

        var act = async () => await h.IngestAsync(path);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // ---- search ----

    [SkippableFact]
    public async Task Search_RanksNearestChunkFirst_WithCosineSimilarityScores()
    {
        using var h = await NewHarnessAsync();
        h.Write("corpus.txt", "alpha beta gamma\n\ndelta epsilon zeta\n\neta theta iota\n\nkappa lambda mu");
        await h.IngestAsync(Path.Combine(h.ContentDir, "corpus.txt"));

        // The mock embeds identical text identically, so the chunk is also the nearest vector (cosine similarity 1).
        var results = await h.Search.SearchAsync("alpha beta gamma", 4);

        results.Should().HaveCount(4);
        results[0].Content.Should().Be("alpha beta gamma");
        results[0].Score.Should().BeApproximately(1f, 1e-4f);
        results.Select(r => r.Score).Should().BeInDescendingOrder();
        results.Skip(1).Should().OnlyContain(r => r.Score < results[0].Score);
    }

    [SkippableFact]
    public async Task Search_ReturnsNearestChunks_EvenWhenNoWordsMatch()
    {
        using var h = await NewHarnessAsync();
        h.Write("a.txt", "completely different words");
        await h.IngestAsync(Path.Combine(h.ContentDir, "a.txt"));

        var results = await h.Search.SearchAsync("zzzqqq", 5);

        results.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Search_RespectsMaxResults()
    {
        using var h = await NewHarnessAsync();
        h.Write("a.txt", string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"token paragraph {i}")));
        await h.IngestAsync(Path.Combine(h.ContentDir, "a.txt"));

        (await h.Search.SearchAsync("token", 3)).Should().HaveCount(3);
    }

    [SkippableFact]
    public async Task Search_FiltersByDocumentIds()
    {
        using var h = await NewHarnessAsync();
        h.Write("first.txt", "shared phrase one");
        h.Write("second.txt", "shared phrase two");
        var results = await h.IngestAllAsync();
        var second = results.Single(r => r.FilePath == "second.txt");

        var filtered = await h.Search.SearchAsync("shared phrase", 10, [second.DocumentId]);

        filtered.Should().ContainSingle();
        filtered[0].DocumentId.Should().Be(second.DocumentId);
        (await h.Search.SearchAsync("shared phrase", 10)).Should().HaveCount(2);
        (await h.Search.SearchAsync("shared phrase", 10, [Guid.NewGuid()])).Should().BeEmpty();
        (await h.Search.SearchAsync("shared phrase", 10, results.Select(r => r.DocumentId).ToList())).Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task Search_ReturnsEmpty_WhenNothingIngested()
    {
        using var h = await NewHarnessAsync();

        (await h.Search.SearchAsync("anything", 5)).Should().BeEmpty();
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Search_RejectsInvalidMaxResults(int maxResults)
    {
        using var h = await NewHarnessAsync();

        var act = async () => await h.Search.SearchAsync("q", maxResults);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_RejectsBlankQuery(string query)
    {
        using var h = await NewHarnessAsync();

        var act = async () => await h.Search.SearchAsync(query, 5);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [SkippableFact]
    public async Task Search_RejectsOverlongQuery_AndTooManyDocumentIds()
    {
        using var h = await NewHarnessAsync();

        var tooLong = async () => await h.Search.SearchAsync(new string('a', SearchService.MaxQueryLength + 1), 5);
        var tooMany = async () => await h.Search.SearchAsync("q", 5, Enumerable.Range(0, SearchService.MaxDocumentIds + 1).Select(_ => Guid.NewGuid()).ToList());

        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooMany.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}

/// <summary>Unit-level run of the behaviour suite against the in-memory store.</summary>
public class InMemoryPipelineTests : PipelineBehaviorTests
{
    protected override Task<StoreUnderTest> CreateStoreAsync()
    {
        var store = new InMemoryDocumentStore();
        return Task.FromResult(new StoreUnderTest(store, path => Task.FromResult(store.CreateDocument(path))));
    }

    [Fact]
    public async Task IngestFile_PersistsTitleHashAndModality()
    {
        using var h = new PipelineHarness(new InMemoryDocumentStore());
        var path = h.Write("My Notes.txt", "hello");

        var result = await h.IngestAsync(path);

        var draft = ((InMemoryDocumentStore)h.Store).DraftOf(result.DocumentId)!;
        draft.Title.Should().Be("My Notes.txt");
        draft.ContentHash.Should().Be("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
        draft.Modality.Should().Be(Modality.Text);
    }

    [Fact]
    public async Task Ingest_DoesNotCreateTheVectorIndex_TheWorkerDoesThatOncePerJob()
    {
        using var h = new PipelineHarness(new InMemoryDocumentStore());
        var store = (InMemoryDocumentStore)h.Store;

        await h.IngestAsync(h.Write("a.txt", "x"));

        store.EnsureVectorIndexCalls.Should().Be(0);
    }
}
