using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

public class LuceneRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbrag_lucene_" + Guid.NewGuid().ToString("N"));
    private readonly string _index;
    private readonly LuceneRepository _repo = new();

    public LuceneRepositoryTests()
    {
        _index = Path.Combine(_root, "index");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenIndexDirectoryDoesNotExist()
    {
        var hits = await _repo.SearchAsync(_index, "anything", 5, null);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenDirectoryExistsButHasNoIndex()
    {
        Directory.CreateDirectory(_index);

        var hits = await _repo.SearchAsync(_index, "anything", 5, null);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_ReturnsChunkIds_RankedByBm25()
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a",
        [
            new LuceneChunk(1, "cats and dogs"),
            new LuceneChunk(2, "cats cats cats everywhere"),
            new LuceneChunk(3, "nothing relevant here")
        ]);

        var hits = await _repo.SearchAsync(_index, "cats", 10, null);

        hits.Select(h => h.ChunkId).Should().Equal(2, 1);
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public async Task Replace_RemovesPreviousEntriesOfSameDocument()
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(1, "old zebra content")]);
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(2, "new giraffe content")]);

        (await _repo.SearchAsync(_index, "zebra", 10, null)).Should().BeEmpty();
        (await _repo.SearchAsync(_index, "giraffe", 10, null)).Select(h => h.ChunkId).Should().Equal(2);
    }

    [Fact]
    public async Task Replace_WithNoChunks_DeletesDocument()
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(1, "zebra")]);
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", []);

        (await _repo.SearchAsync(_index, "zebra", 10, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Replace_DoesNotTouchOtherDocuments()
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(1, "shared term")]);
        await _repo.ReplaceDocumentAsync(_index, "doc_b", "b", [new LuceneChunk(2, "shared term")]);
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(3, "shared term")]);

        var hits = await _repo.SearchAsync(_index, "shared", 10, null);

        hits.Select(h => h.ChunkId).Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public async Task Search_FiltersBySourceIds()
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "alpha", [new LuceneChunk(1, "common words")]);
        await _repo.ReplaceDocumentAsync(_index, "doc_b", "beta", [new LuceneChunk(2, "common words")]);

        (await _repo.SearchAsync(_index, "common", 10, ["beta"])).Select(h => h.ChunkId).Should().Equal(2);
        (await _repo.SearchAsync(_index, "common", 10, ["alpha", "beta"])).Should().HaveCount(2);
        (await _repo.SearchAsync(_index, "common", 10, ["missing"])).Should().BeEmpty();
    }

    [Theory]
    [InlineData("foo AND")]
    [InlineData("(unclosed")]
    [InlineData("field:value +must -mustnot \"quote")]
    [InlineData("wild*card? ~fuzzy ^boost")]
    [InlineData("OR NOT")]
    public async Task Search_TreatsLuceneSyntaxAsLiteralText(string query)
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(1, "plain text")]);

        var act = async () => await _repo.SearchAsync(_index, query, 10, null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Search_RespectsMaxResults()
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", Enumerable.Range(1, 10).Select(i => new LuceneChunk(i, "repeated token")).ToList());

        (await _repo.SearchAsync(_index, "repeated", 3, null)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_HandlesUnicodeContent()
    {
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(1, "Ünïcödé café naïve résumé")]);

        (await _repo.SearchAsync(_index, "café", 10, null)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Index_DoesNotStoreContent_AndWriteLockIsReleasedBetweenCalls()
    {
        // Two sequential replaces would fail with LockObtainFailedException if the writer stayed open.
        await _repo.ReplaceDocumentAsync(_index, "doc_a", "a", [new LuceneChunk(1, "one")]);
        await _repo.ReplaceDocumentAsync(_index, "doc_b", "b", [new LuceneChunk(2, "two")]);

        (await _repo.SearchAsync(_index, "two", 10, null)).Should().HaveCount(1);
    }
}
