using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

/// <summary>
/// End-to-end ingest + hybrid search behaviour against the <see cref="IDocumentStore"/> contract.
/// Subclasses choose the store: in-memory (unit) or real SQL Server (integration).
/// </summary>
public abstract class PipelineBehaviorTests : IDisposable
{
    private readonly List<PipelineHarness> _harnesses = new();

    protected abstract Task<IDocumentStore> CreateStoreAsync();

    protected async Task<PipelineHarness> NewHarnessAsync()
    {
        var harness = new PipelineHarness(await CreateStoreAsync());
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

        var result = await h.Ingester.IngestFileAsync(path);

        result.Modality.Should().Be(Modality.Text);
        result.ChunkCount.Should().Be(2);
        result.DocumentId.Should().Be(DocumentIds.FromSourcePath(path));

        var results = await h.Search.SearchAsync("bananas", 5);
        var banana = results.Single(r => r.Content!.Contains("bananas"));
        banana.Ordinal.Should().Be(2);
        banana.SourceId.Should().Be("fruit");
        banana.SourceFile.Should().Be("fruit.txt");
        banana.SourcePath.Should().Be(Path.GetFullPath(path));
        content.Substring((int)banana.LocatorStart!.Value, (int)(banana.LocatorEnd!.Value - banana.LocatorStart.Value))
            .Should().Be(banana.Content);
    }

    [SkippableFact]
    public async Task Reingest_ReplacesPreviousContent_InSqlAndLucene()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("doc.txt", "zebra stripes\n\nzebra herd");
        await h.Ingester.IngestFileAsync(path);
        (await h.Search.SearchAsync("zebra", 10)).Should().HaveCount(2);

        File.WriteAllText(path, "giraffe neck");
        var second = await h.Ingester.IngestFileAsync(path);

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

        await h.Ingester.IngestFolderAsync(h.ContentDir);

        var results = await h.Search.SearchAsync("one two three x y H1 H2", 50);
        results.Select(r => r.ChunkId).Should().OnlyHaveUniqueItems();
        results.Should().NotBeEmpty();
    }

    [SkippableFact]
    public async Task IngestFile_Image_IsOneVectorOnlyChunk_AndCreatesNoLuceneIndex()
    {
        using var h = await NewHarnessAsync();
        var path = h.WriteBytes("photo.png", [1, 2, 3, 4, 5]);

        var result = await h.Ingester.IngestFileAsync(path);

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

        var result = await h.Ingester.IngestFileAsync(path);

        result.Modality.Should().Be(Modality.Audio);
        result.ChunkCount.Should().Be(3);
        var hits = await h.Search.SearchAsync("spoken words", 10);
        hits.Should().HaveCount(3);
        hits.OrderBy(r => r.Ordinal).Select(r => (r.LocatorStart, r.LocatorEnd))
            .Should().Equal((0L, 30_000L), (30_000L, 60_000L), (60_000L, 90_000L));
        hits.Should().OnlyContain(r => r.Modality == Modality.Audio && r.Content == null);
    }

    [SkippableFact]
    public async Task IngestFolder_RoutesByExtension_AndIgnoresUnsupportedFiles()
    {
        using var h = await NewHarnessAsync();
        h.Write("notes.txt", "plain notes");
        h.Write("sub/readme.md", "# Title\nmarkdown body");
        h.WriteBytes("sub/deep/pic.jpg", [1, 2, 3]);
        h.WriteBytes("clip.mp3", [4, 5, 6]);
        h.Write("ignored.pdf", "not supported");

        var summary = await h.Ingester.IngestFolderAsync(h.ContentDir);

        summary.Failed.Should().BeEmpty();
        summary.Succeeded.Select(s => Path.GetFileName(s.FilePath)).Should().BeEquivalentTo("notes.txt", "readme.md", "pic.jpg", "clip.mp3");
        summary.Succeeded.Select(s => s.Modality).Order().Should().Equal(Modality.Text, Modality.Text, Modality.Image, Modality.Audio);
    }

    [SkippableFact]
    public async Task IngestFolder_ReportsPerFileFailures_WithoutAbortingTheBatch()
    {
        using var h = await NewHarnessAsync();
        h.Write("good.txt", "good content");
        h.Write("bad.txt", new string('x', 500));

        var summary = await h.Ingester.IngestFolderAsync(h.ContentDir, new IngestOptions { MaxChunkCharacters = 100 });

        summary.Succeeded.Should().ContainSingle(s => s.FilePath.EndsWith("good.txt"));
        summary.Failed.Should().ContainSingle(f => f.FilePath.EndsWith("bad.txt") && f.Error.Contains("exceeding"));
    }

    [SkippableFact]
    public async Task IngestFolder_CountsEmptyFilesAsSkipped()
    {
        using var h = await NewHarnessAsync();
        h.Write("empty.txt", "   \n\n  ");
        h.Write("full.txt", "content");

        var summary = await h.Ingester.IngestFolderAsync(h.ContentDir);

        summary.Skipped.Should().Be(1);
        summary.Succeeded.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task IngestFolder_EmptyFolder_ReturnsEmptySummary()
    {
        using var h = await NewHarnessAsync();

        var summary = await h.Ingester.IngestFolderAsync(h.ContentDir);

        summary.Succeeded.Should().BeEmpty();
        summary.Failed.Should().BeEmpty();
    }

    // ---- ingestion safety ----

    [SkippableFact]
    public async Task IngestFile_Throws_WhenFileMissing()
    {
        using var h = await NewHarnessAsync();

        var act = async () => await h.Ingester.IngestFileAsync(Path.Combine(h.ContentDir, "missing.txt"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [SkippableFact]
    public async Task IngestFolder_Throws_WhenFolderMissing()
    {
        using var h = await NewHarnessAsync();

        var act = async () => await h.Ingester.IngestFolderAsync(Path.Combine(h.ContentDir, "nope"));

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [SkippableFact]
    public async Task IngestFile_RejectsFilesOverSizeLimit()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("big.txt", new string('a', 2048));

        var act = async () => await h.Ingester.IngestFileAsync(path, new IngestOptions { MaxFileSizeBytes = 1024 });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeding the limit*");
    }

    [SkippableFact]
    public async Task IngestFile_RejectsChunksOverLimit()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("long.txt", new string('a', 500));

        var act = async () => await h.Ingester.IngestFileAsync(path, new IngestOptions { MaxChunkCharacters = 100 });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*chunk*");
    }

    [SkippableFact]
    public async Task IngestFolder_RejectsFoldersOverFileLimit()
    {
        using var h = await NewHarnessAsync();
        h.Write("1.txt", "a");
        h.Write("2.txt", "b");
        h.Write("3.txt", "c");

        var act = async () => await h.Ingester.IngestFolderAsync(h.ContentDir, new IngestOptions { MaxFiles = 2 });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeding the limit of 2*");
    }

    [SkippableTheory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public async Task IngestFile_RejectsInvalidOptions(long maxBytes, int maxFiles, int maxChunk)
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("a.txt", "x");

        var act = async () => await h.Ingester.IngestFileAsync(path, new IngestOptions { MaxFileSizeBytes = maxBytes, MaxFiles = maxFiles, MaxChunkCharacters = maxChunk });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [SkippableFact]
    public async Task IngestFolder_SkipsSymlinkedFilesAndDirectoriesByDefault()
    {
        using var h = await NewHarnessAsync();
        h.Write("real.txt", "real content");
        var outside = Path.Combine(h.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret content");
        TryLink(() => File.CreateSymbolicLink(Path.Combine(h.ContentDir, "link.txt"), Path.Combine(outside, "secret.txt")));
        TryLink(() => Directory.CreateSymbolicLink(Path.Combine(h.ContentDir, "linkdir"), outside));

        var summary = await h.Ingester.IngestFolderAsync(h.ContentDir);

        summary.Succeeded.Should().ContainSingle(s => s.FilePath.EndsWith("real.txt"));
        (await h.Search.SearchAsync("secret", 10)).Should().NotContain(r => r.Content!.Contains("secret"));
    }

    [SkippableFact]
    public async Task IngestFolder_FollowsSymlinks_WhenAllowed()
    {
        using var h = await NewHarnessAsync();
        var outside = Path.Combine(h.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret content");
        TryLink(() => File.CreateSymbolicLink(Path.Combine(h.ContentDir, "link.txt"), Path.Combine(outside, "secret.txt")));

        var summary = await h.Ingester.IngestFolderAsync(h.ContentDir, new IngestOptions { AllowSymlinks = true });

        summary.Succeeded.Should().ContainSingle(s => s.FilePath.EndsWith("link.txt"));
    }

    [SkippableFact]
    public async Task IngestFile_RefusesSymlink_ByDefault()
    {
        using var h = await NewHarnessAsync();
        var target = h.Write("target.txt", "content");
        var link = Path.Combine(h.ContentDir, "link.txt");
        TryLink(() => File.CreateSymbolicLink(link, target));

        var act = async () => await h.Ingester.IngestFileAsync(link);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*symbolic link*");
    }

    [SkippableFact]
    public async Task IngestFile_Throws_ForUnsupportedExtension()
    {
        using var h = await NewHarnessAsync();
        var path = h.Write("doc.pdf", "x");

        var act = async () => await h.Ingester.IngestFileAsync(path);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static void TryLink(Action create)
    {
        try { create(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new SkipException("Symbolic links are not available: " + ex.Message);
        }
    }

    // ---- search ----

    [SkippableFact]
    public async Task Search_HybridRanking_PutsChunkMatchingBothSidesFirst()
    {
        using var h = await NewHarnessAsync();
        h.Write("corpus.txt", "alpha beta gamma\n\ndelta epsilon zeta\n\neta theta iota\n\nkappa lambda mu");
        await h.Ingester.IngestFileAsync(Path.Combine(h.ContentDir, "corpus.txt"));

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
        await h.Ingester.IngestFileAsync(Path.Combine(h.ContentDir, "a.txt"));

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
        await h.Ingester.IngestFileAsync(Path.Combine(h.ContentDir, "a.txt"));

        (await h.Search.SearchAsync("token", 3)).Should().HaveCount(3);
    }

    [SkippableFact]
    public async Task Search_FiltersBySourceIds_OnBothSides()
    {
        using var h = await NewHarnessAsync();
        h.Write("first.txt", "shared phrase one");
        h.Write("second.txt", "shared phrase two");
        await h.Ingester.IngestFolderAsync(h.ContentDir);

        var filtered = await h.Search.SearchAsync("shared phrase", 10, ["second"]);

        filtered.Should().ContainSingle();
        filtered[0].SourceId.Should().Be("second");
        (await h.Search.SearchAsync("shared phrase", 10)).Should().HaveCount(2);
        (await h.Search.SearchAsync("shared phrase", 10, ["nonexistent"])).Should().BeEmpty();
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
        await h.Ingester.IngestFileAsync(Path.Combine(h.ContentDir, "a.txt"));

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
    public async Task Search_RejectsOverlongQuery_AndBadSourceIds()
    {
        using var h = await NewHarnessAsync();

        var tooLong = async () => await h.Search.SearchAsync(new string('a', SearchService.MaxQueryLength + 1), 5);
        var tooMany = async () => await h.Search.SearchAsync("q", 5, Enumerable.Range(0, SearchService.MaxSourceIds + 1).Select(i => "s" + i).ToList());
        var blank = async () => await h.Search.SearchAsync("q", 5, [" "]);
        var longId = async () => await h.Search.SearchAsync("q", 5, [new string('s', SearchService.MaxSourceIdLength + 1)]);

        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooMany.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await blank.Should().ThrowAsync<ArgumentException>();
        await longId.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}

/// <summary>Unit-level run of the behaviour suite against the in-memory store.</summary>
public class InMemoryPipelineTests : PipelineBehaviorTests
{
    protected override Task<IDocumentStore> CreateStoreAsync() => Task.FromResult<IDocumentStore>(new InMemoryDocumentStore());

    [Fact]
    public async Task IngestFile_PersistsTitleHashAndModality()
    {
        using var h = new PipelineHarness(new InMemoryDocumentStore());
        var path = h.Write("My Notes.txt", "hello");

        var result = await h.Ingester.IngestFileAsync(path);

        var draft = ((InMemoryDocumentStore)h.Store).DraftOf(result.DocumentId)!;
        draft.Title.Should().Be("My Notes.txt");
        draft.SourceId.Should().Be("My Notes");
        draft.ContentHash.Should().Be("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
        draft.Modality.Should().Be(Modality.Text);
    }

    [Fact]
    public async Task IngestFile_EnsuresVectorIndexOnce()
    {
        using var h = new PipelineHarness(new InMemoryDocumentStore());
        var store = (InMemoryDocumentStore)h.Store;

        await h.Ingester.IngestFileAsync(h.Write("a.txt", "x"));

        store.EnsureVectorIndexCalls.Should().Be(1);
    }

    [Fact]
    public async Task IngestFolder_EnsuresVectorIndexOncePerBatch()
    {
        using var h = new PipelineHarness(new InMemoryDocumentStore());
        var store = (InMemoryDocumentStore)h.Store;
        for (int i = 0; i < 5; i++) h.Write($"f{i}.txt", $"content {i}\n\nmore {i}");

        await h.Ingester.IngestFolderAsync(h.ContentDir);

        store.ReplaceCalls.Should().Be(5);
        store.EnsureVectorIndexCalls.Should().Be(1);
    }

    [Fact]
    public async Task IngestFile_MarksDocumentFailed_WhenLuceneWriteFails()
    {
        var store = new InMemoryDocumentStore();
        using var h = new PipelineHarness(store, new FailingLuceneRepository());
        var path = h.Write("a.txt", "content");

        var act = async () => await h.Ingester.IngestFileAsync(path);

        await act.Should().ThrowAsync<IOException>();
        store.StatusOf(DocumentIds.FromSourcePath(path)).Should().Be(IndexStatus.Failed);
    }

    [Fact]
    public async Task IngestFile_DoesNotCallLuceneForMediaFiles()
    {
        // A failing Lucene repository must not affect vector-only media ingestion.
        var store = new InMemoryDocumentStore();
        using var h = new PipelineHarness(store, new FailingLuceneRepository());

        var result = await h.Ingester.IngestFileAsync(h.WriteBytes("p.png", [1, 2, 3]));

        result.ChunkCount.Should().Be(1);
        store.StatusOf(result.DocumentId).Should().Be(IndexStatus.Indexed);
    }

    [Fact]
    public async Task Search_SkipsStaleLuceneEntriesMissingFromStore()
    {
        var store = new InMemoryDocumentStore();
        using var h = new PipelineHarness(store);
        var path = h.Write("a.txt", "orphan words");
        await h.Ingester.IngestFileAsync(path);

        // Re-ingest into a fresh store while keeping the old Lucene index: ids no longer resolve.
        using var h2 = new PipelineHarness(new InMemoryDocumentStore());
        Directory.Move(h.IndexPath, h2.IndexPath);

        (await h2.Search.SearchAsync("orphan", 5)).Should().BeEmpty();
    }
}
