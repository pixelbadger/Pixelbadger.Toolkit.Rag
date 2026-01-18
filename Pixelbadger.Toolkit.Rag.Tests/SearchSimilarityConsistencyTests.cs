using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class SearchSimilarityConsistencyTests : IDisposable
{
    private readonly SearchIndexer _indexer;
    private readonly string _testDirectory;
    private readonly string _indexPath;

    public SearchSimilarityConsistencyTests()
    {
        var luceneRepo = new LuceneRepository();
        var vectorRepo = new VectorRepository(new MockEmbeddingService());
        var reranker = new RrfReranker();
        var chunkers = new List<ITextChunker>
        {
            new MarkdownTextChunker(),
            new ParagraphTextChunker()
        };
        var chunkerFactory = new ChunkerFactory(chunkers);
        var fileReaders = new List<IFileReader>
        {
            new PlainTextFileReader(),
            new MarkdownFileReader()
        };
        var fileReaderFactory = new FileReaderFactory(fileReaders);
        _indexer = new SearchIndexer(luceneRepo, vectorRepo, reranker, chunkerFactory, fileReaderFactory);
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _indexPath = Path.Combine(_testDirectory, "similarity-test-index");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public async Task BM25Similarity_ShouldProduceConsistentScoring_WhenIndexingAndQuerying()
    {
        // Create documents with known term frequency patterns to test BM25 scoring
        var contentFile = Path.Combine(_testDirectory, "bm25-test.txt");
        var content = @"Document one contains keyword keyword keyword keyword.

Document two contains keyword once.

Document three has many other words but contains keyword twice keyword.

Document four discusses completely different topics without the search term.

Final document has keyword appearing three times: keyword keyword keyword.";

        await File.WriteAllTextAsync(contentFile, content);

        // Index the content (should use BM25 similarity)
        await _indexer.IngestContentAsync(_indexPath, contentFile);

        // Query for the keyword (should use same BM25 similarity)
        var results = await _indexer.SearchAsync(_indexPath, "keyword", SearchMode.Bm25, 10, null);

        // Verify BM25 behavior: documents with higher term frequency should generally score higher
        // but BM25 has diminishing returns for very high frequencies
        results.Should().NotBeEmpty();
        results.Should().HaveCountGreaterThanOrEqualTo(4); // Should find 4 documents with "keyword"

        // Verify results are ordered by score (descending)
        results.Should().BeInDescendingOrder(r => r.Score);

        // Document with 4 occurrences should score highest (but BM25 has saturation)
        var topResult = results[0];
        topResult.Content.Should().Contain("keyword keyword keyword keyword");

        // Document with no keyword should not appear in results
        results.Should().NotContain(r => r.Content.Contains("completely different topics"));
    }

    [Fact]
    public async Task BM25Similarity_ShouldHandleDocumentLengthNormalization_WhenDocumentsVaryInSize()
    {
        var contentFile = Path.Combine(_testDirectory, "length-test.txt");
        var content = @"Short document with target.

This is a much longer document that contains many words and phrases and sentences and paragraphs but still contains the target term somewhere in the middle of all this verbose text that goes on and on with lots of additional content.

Medium length document that mentions target in reasonable context.";

        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "target", SearchMode.Bm25, 10, null);

        results.Should().NotBeEmpty();
        results.Should().HaveCount(3);

        // BM25 should normalize for document length
        // Shorter documents with the same term frequency should generally score higher
        var shortDoc = results.FirstOrDefault(r => r.Content.StartsWith("Short document"));
        var longDoc = results.FirstOrDefault(r => r.Content.StartsWith("This is a much longer"));

        shortDoc.Should().NotBeNull();
        longDoc.Should().NotBeNull();

        // Short document should score higher than or equal to long document due to BM25 length normalization
        shortDoc!.Score.Should().BeGreaterThanOrEqualTo(longDoc!.Score);
    }

    [Fact]
    public async Task BM25Similarity_ShouldHandleMultiTermQueries_WhenUsingBooleanOperators()
    {
        var contentFile = Path.Combine(_testDirectory, "multiterm-test.txt");
        var content = @"Document about cats and dogs together.

Document only about cats.

Document only about dogs.

Document about birds and fish.

Document about cats dogs and other pets.";

        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "cats AND dogs", SearchMode.Bm25, 10, null);

        results.Should().NotBeEmpty();

        // Documents containing both terms should score higher
        var bothTermsResults = results.Where(r =>
            r.Content.Contains("cats", StringComparison.OrdinalIgnoreCase) &&
            r.Content.Contains("dogs", StringComparison.OrdinalIgnoreCase)).ToList();

        bothTermsResults.Should().NotBeEmpty();

        // Documents with both terms should generally score higher than single-term documents
        if (results.Count > bothTermsResults.Count)
        {
            var topScores = results.Take(bothTermsResults.Count).Select(r => r.Score);
            var expectedTopResults = bothTermsResults.Select(r => r.Score);

            // The highest scoring results should include documents with both terms
            topScores.Should().ContainInOrder(expectedTopResults.OrderByDescending(s => s));
        }
    }

    [Fact]
    public async Task BM25Similarity_ShouldHandleTermFrequencySaturation_WhenTermsRepeatedExcessively()
    {
        var contentFile = Path.Combine(_testDirectory, "saturation-test.txt");
        var repetitive = string.Join(" ", Enumerable.Repeat("spam", 100));
        var content = $@"Document with moderate spam spam spam usage.

Document with excessive repetition: {repetitive}.

Document with single spam occurrence.";

        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "spam", SearchMode.Bm25, 10, null);

        results.Should().NotBeEmpty();
        results.Should().HaveCount(3);

        // BM25 should show diminishing returns for excessive repetition
        // The document with 100 occurrences shouldn't score 100x higher than moderate usage
        var excessiveDoc = results.FirstOrDefault(r => r.Content.Contains("excessive repetition"));
        var moderateDoc = results.FirstOrDefault(r => r.Content.Contains("moderate spam spam spam"));
        var singleDoc = results.FirstOrDefault(r => r.Content.Contains("single spam occurrence"));

        excessiveDoc.Should().NotBeNull();
        moderateDoc.Should().NotBeNull();
        singleDoc.Should().NotBeNull();

        // Excessive document should score highest, but not excessively higher due to BM25 saturation
        excessiveDoc!.Score.Should().BeGreaterThan(moderateDoc!.Score);
        moderateDoc.Score.Should().BeGreaterThan(singleDoc!.Score);

        // The ratio between excessive and moderate should be reasonable (BM25 k1 parameter effect)
        var excessiveToModerateRatio = excessiveDoc.Score / moderateDoc.Score;
        excessiveToModerateRatio.Should().BeLessThan(10); // Should not be extremely high due to saturation
    }

    [Fact]
    public async Task BM25Similarity_ShouldProduceReproducibleScores_WhenQueryingMultipleTimes()
    {
        var contentFile = Path.Combine(_testDirectory, "reproducible-test.txt");
        var content = @"Consistent document for reproducibility testing.

Another document with different content for testing.";

        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);

        // Run the same query multiple times
        var results1 = await _indexer.SearchAsync(_indexPath, "testing", SearchMode.Bm25, 10, null);
        var results2 = await _indexer.SearchAsync(_indexPath, "testing", SearchMode.Bm25, 10, null);
        var results3 = await _indexer.SearchAsync(_indexPath, "testing", SearchMode.Bm25, 10, null);

        // Results should be identical across runs
        results1.Should().HaveCount(results2.Count);
        results2.Should().HaveCount(results3.Count);

        for (int i = 0; i < results1.Count; i++)
        {
            results1[i].Score.Should().Be(results2[i].Score);
            results2[i].Score.Should().Be(results3[i].Score);
            results1[i].DocumentId.Should().Be(results2[i].DocumentId);
            results2[i].DocumentId.Should().Be(results3[i].DocumentId);
        }
    }
}
