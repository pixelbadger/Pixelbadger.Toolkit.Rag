using System.Text;
using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>
/// End-to-end ingest + hybrid search behaviour against the <see cref="IDocumentStore"/> contract.
/// Subclasses choose the store: in-memory (unit) or real SQL Server (integration).
/// </summary>
public abstract class PipelineBehaviorTests : IDisposable
{
    private readonly List<PipelineHarness> _harnesses = new();

    /// <summary>The store under test and how to create the (Queued) document an ingest writes into.</summary>
    protected sealed record StoreUnderTest(IDocumentStore Store, Func<string, Task<Guid>> CreateDocument);

    protected abstract Task<StoreUnderTest> CreateStoreAsync();

    protected async Task<PipelineHarness> NewHarnessAsync(Func<ILuceneRepository, ILuceneRepository>? decorateLucene = null)
    {
        var (store, createDocument) = await CreateStoreAsync();
        var harness = new PipelineHarness(store, decorateLucene?.Invoke(new LuceneRepository()), createDocument);
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
    public async Task Reingest_ReplacesPreviousContent_InSqlAndLucene()
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
    public async Task IngestFile_Image_IsOneVectorOnlyChunk_AndCreatesNoLuceneIndex()
    {
        using var h = await NewHarnessAsync();
        var path = h.WriteBytes("photo.png", [1, 2, 3, 4, 5]);

        var result = await h.IngestAsync(path);

        result.Modality.Should().Be(Modality.Image);
        result.ChunkCount.Should().Be(1);
        Directory.Exists(h.IndexPath).Should().BeFalse("image chunks are not indexed in Lucene");

        // A media-only corpus is still searchable (vector side only).
        var hits = await h.Search.SearchAsync("a photo", 5);
        var hit = hits.Single();
        hit.Modality.Should().Be(Modality.Image);
        hit.Content.Should().BeNull();
        hit.LocatorStart.Should().BeNull();
        hit.KeywordRank.Should().BeNull();
        hit.VectorRank.Should().Be(1);
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
        Directory.Exists(h.IndexPath).Should().BeFalse("nothing reached Lucene");
    }

    // ---- canonical source content ----

    [SkippableFact]
    public async Task SourceContent_NewDocumentHasNone_UntilItsFirstSuccessfulIngest()
    {
        using var h = await NewHarnessAsync();
        var documentId = await h.NewDocumentAsync("fresh.txt");

        (await h.Store.GetContentAsync(documentId)).Should().BeNull();
        (await h.Store.GetContentAsync(Guid.NewGuid())).Should().BeNull("unknown document");

        await h.Ingester.IngestAsync(new IngestSource(h.Write("fresh.txt", "hello"), "fresh.txt", documentId));

        (await h.Store.GetContentAsync(documentId)).Should().NotBeNull();
    }

    [SkippableFact]
    public async Task SourceContent_SuccessfulIngest_StoresTheExactBytes_MediaTypeAndLogicalFileName()
    {
        using var h = await NewHarnessAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0x0D, 0x0A, 0x1A];
        var text = h.Write("docs/guide.md", "# Guide\r\n\r\nUmlaut ü and crlf line endings.");
        var image = h.WriteBytes("img/cat.PNG", png);

        var textResult = await h.IngestAsync(text);
        var imageResult = await h.IngestAsync(image);

        var textContent = (await h.Store.GetContentAsync(textResult.DocumentId))!;
        textContent.Bytes.Should().Equal(await File.ReadAllBytesAsync(text));
        textContent.ContentType.Should().Be("text/markdown");
        textContent.FileName.Should().Be("guide.md", "the last segment of the logical path");
        var imageContent = (await h.Store.GetContentAsync(imageResult.DocumentId))!;
        imageContent.Bytes.Should().Equal(png);
        imageContent.ContentType.Should().Be("image/png");
        imageContent.FileName.Should().Be("cat.PNG");
    }

    [SkippableFact]
    public async Task SourceContent_SuccessfulReingest_ReplacesTheSource()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("doc.txt", "version one");
        var first = await h.IngestAsync(path);

        File.WriteAllText(path, "version two");
        await h.IngestAsync(path, documentId: first.DocumentId);

        var content = (await h.Store.GetContentAsync(first.DocumentId))!;
        Encoding.UTF8.GetString(content.Bytes).Should().Be("version two");
    }

    [SkippableFact]
    public async Task SourceContent_FailedReingest_LeavesTheOldSourceAndChunksUntouched()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("doc.txt", "short original text");
        var first = await h.IngestAsync(path);

        File.WriteAllText(path, "this replacement is far too large for the limit");
        var act = async () => await h.IngestAsync(path, new IngestOptions { MaxFileSizeBytes = 25 }, first.DocumentId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Encoding.UTF8.GetString((await h.Store.GetContentAsync(first.DocumentId))!.Bytes).Should().Be("short original text");
        (await h.Search.SearchAsync("original", 5)).Should().ContainSingle().Which.Content.Should().Be("short original text");
    }

    [SkippableFact]
    public async Task SourceContent_IngestThatYieldsNoChunks_StillPromotesTheSource_SoSourceAndIndexAgree()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("doc.txt", "searchable words");
        var first = await h.IngestAsync(path);

        File.WriteAllText(path, "   \n\n  ");
        var result = await h.IngestAsync(path, documentId: first.DocumentId);

        result.ChunkCount.Should().Be(0);
        Encoding.UTF8.GetString((await h.Store.GetContentAsync(first.DocumentId))!.Bytes).Should().Be("   \n\n  ");
        (await h.Search.SearchAsync("searchable", 5)).Should().BeEmpty("the previous version's chunks were replaced too");
    }

    [SkippableFact]
    public async Task SourceContent_IsRemovedWithItsDocument()
    {
        using var h = await NewHarnessAsync();
        var result = await h.IngestAsync(h.Write("gone.txt", "bye"));
        (await h.Store.GetContentAsync(result.DocumentId)).Should().NotBeNull();

        await h.NewDocumentService().DeleteAsync(result.DocumentId);

        (await h.Store.GetContentAsync(result.DocumentId)).Should().BeNull();
    }

    // ---- delete ----

    [SkippableFact]
    public async Task DeleteDocument_RemovesItsChunksFromSqlAndLucene_AndLeavesOthers()
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
        (await h.Lucene.SearchAsync(h.IndexPath, "drop", 10, null)).Should().BeEmpty();
        (await h.NewDocumentService().DeleteAsync(drop.DocumentId)).Should().Be(DeleteOutcome.NotFound);
    }

    [SkippableFact]
    public async Task DeleteDocument_LandingBetweenTheSqlAndLuceneWrites_LeavesNothingBehind()
    {
        PausingLuceneRepository? pausing = null;
        using var h = await NewHarnessAsync(inner => pausing = new PausingLuceneRepository(inner));
        var path = h.Write("race.txt", "racing words");
        var documentId = await h.NewDocumentAsync("race.txt");

        var ingest = h.Ingester.IngestAsync(new IngestSource(path, "race.txt", documentId));
        await pausing!.Entered.WaitAsync(TimeSpan.FromSeconds(10)); // SQL written, Lucene not yet
        var delete = h.NewDocumentService().DeleteAsync(documentId);
        await Task.Delay(200);

        delete.IsCompleted.Should().BeFalse("the delete waits for the ingest's SQL + Lucene writes");
        pausing.Release();
        await ingest;
        (await delete).Should().Be(DeleteOutcome.Deleted);

        (await h.Search.SearchAsync("racing", 5)).Should().BeEmpty();
        (await h.Lucene.SearchAsync(h.IndexPath, "racing", 5, null)).Should().BeEmpty("the delete also removed the Lucene entries");
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
    public async Task Search_HybridRanking_PutsChunkMatchingBothSidesFirst()
    {
        using var h = await NewHarnessAsync();
        h.Write("corpus.txt", "alpha beta gamma\n\ndelta epsilon zeta\n\neta theta iota\n\nkappa lambda mu");
        await h.IngestAsync(Path.Combine(h.ContentDir, "corpus.txt"));

        // The mock embeds identical text identically, so the chunk is also the nearest vector.
        var results = await h.Search.SearchAsync("alpha beta gamma", 4);

        results[0].Content.Should().Be("alpha beta gamma");
        results[0].KeywordRank.Should().Be(1);
        results[0].VectorRank.Should().Be(1);
        results[0].Score.Should().BeApproximately(2f / 61, 1e-6f);
        results.Skip(1).Should().OnlyContain(r => r.Score < results[0].Score);
    }

    [SkippableFact]
    public async Task Search_ReturnsVectorHits_WhenNoKeywordMatches()
    {
        using var h = await NewHarnessAsync();
        h.Write("a.txt", "completely different words");
        await h.IngestAsync(Path.Combine(h.ContentDir, "a.txt"));

        var results = await h.Search.SearchAsync("zzzqqq", 5);

        results.Should().ContainSingle();
        results[0].KeywordRank.Should().BeNull();
        results[0].VectorRank.Should().Be(1);
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
    public async Task Search_FiltersByDocumentIds_OnBothSides()
    {
        using var h = await NewHarnessAsync();
        h.Write("first.txt", "shared phrase one");
        h.Write("second.txt", "shared phrase two");
        var results = await h.IngestAllAsync();
        var second = results.Single(r => r.FilePath == "second.txt");

        var filtered = await h.Search.SearchAsync("shared phrase", 10, [second.DocumentId]);

        filtered.Should().ContainSingle();
        filtered[0].DocumentId.Should().Be(second.DocumentId);
        filtered[0].KeywordRank.Should().NotBeNull();
        filtered[0].VectorRank.Should().NotBeNull();
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

    [SkippableFact]
    public async Task Search_TreatsLuceneSyntaxAsLiteralText()
    {
        using var h = await NewHarnessAsync();
        h.Write("a.txt", "some text");
        await h.IngestAsync(Path.Combine(h.ContentDir, "a.txt"));

        var act = async () => await h.Search.SearchAsync("field:\"unclosed AND (", 5);

        await act.Should().NotThrowAsync();
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

    [Fact]
    public async Task IngestFile_MarksDocumentFailed_WhenLuceneWriteFails()
    {
        var store = new InMemoryDocumentStore();
        using var h = new PipelineHarness(store, new FailingLuceneRepository());
        var path = h.Write("a.txt", "content");

        var act = async () => await h.IngestAsync(path);

        await act.Should().ThrowAsync<IOException>();
        store.StatusOf(store.AllDocumentIds.Single()).Should().Be(IndexStatus.Failed);
    }

    [Fact]
    public async Task IngestFile_DoesNotCallLuceneForMediaFiles()
    {
        // A failing Lucene repository must not affect vector-only media ingestion.
        var store = new InMemoryDocumentStore();
        using var h = new PipelineHarness(store, new FailingLuceneRepository());

        var result = await h.IngestAsync(h.WriteBytes("p.png", [1, 2, 3]));

        result.ChunkCount.Should().Be(1);
        store.StatusOf(result.DocumentId).Should().Be(IndexStatus.Indexed);
    }

    [Fact]
    public async Task Search_SkipsStaleLuceneEntriesMissingFromStore()
    {
        var store = new InMemoryDocumentStore();
        using var h = new PipelineHarness(store);
        var path = h.Write("a.txt", "orphan words");
        await h.IngestAsync(path);

        // Re-ingest into a fresh store while keeping the old Lucene index: ids no longer resolve.
        using var h2 = new PipelineHarness(new InMemoryDocumentStore());
        Directory.Move(h.IndexPath, h2.IndexPath);

        (await h2.Search.SearchAsync("orphan", 5)).Should().BeEmpty();
    }
}
