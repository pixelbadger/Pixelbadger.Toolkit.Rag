using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class VectorAndHybridSearchTests : IDisposable
{
    private readonly IContentIngester _ingester;
    private readonly ISearchService _searchService;
    private readonly string _testDirectory;
    private readonly string _indexPath;

    public VectorAndHybridSearchTests()
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
        _ingester = new ContentIngester(luceneRepo, vectorRepo, chunkerFactory, fileReaderFactory);
        _searchService = new SearchService(luceneRepo, vectorRepo, reranker);
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _indexPath = Path.Combine(_testDirectory, "test-index");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public async Task VectorSearch_ShouldReturnResults_WhenMatchingContentExists()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "The quick brown fox jumps over the lazy dog.\n\nThis is a second paragraph about cats and dogs.";
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "fox", SearchMode.Vector, 10);

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Content));
    }

    [Fact]
    public async Task VectorSearch_ShouldReturnResultsWithScores()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "Document about machine learning and AI.";
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "artificial intelligence", SearchMode.Vector, 10);

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => !float.IsNaN(r.Score));
    }

    [Fact]
    public async Task VectorSearch_ShouldRespectMaxResults()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = string.Join("\n\n", Enumerable.Repeat("This is a test paragraph about technology.", 10));
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "technology", SearchMode.Vector, 3);

        results.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task VectorSearch_ShouldThrowFileNotFoundException_WhenNoVectorDatabase()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "Some content.";
        await File.WriteAllTextAsync(contentFile, content);

        // Ingest with vectors disabled so no vector DB is created
        await _ingester.IngestContentAsync(_indexPath, contentFile, new IngestOptions { EnableVectorStorage = false });

        var act = async () => await _searchService.SearchAsync(_indexPath, "content", SearchMode.Vector, 10);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task VectorSearch_ShouldFilterBySourceIds()
    {
        var contentFile1 = Path.Combine(_testDirectory, "doc1.txt");
        var contentFile2 = Path.Combine(_testDirectory, "doc2.txt");

        await File.WriteAllTextAsync(contentFile1, "Content about cats in document one.");
        await File.WriteAllTextAsync(contentFile2, "Content about cats in document two.");

        await _ingester.IngestContentAsync(_indexPath, contentFile1);
        await _ingester.IngestContentAsync(_indexPath, contentFile2);

        var results = await _searchService.SearchAsync(_indexPath, "cats", SearchMode.Vector, 10, new[] { "doc1" });

        results.Should().OnlyContain(r => r.SourceId == "doc1");
    }

    [Fact]
    public async Task HybridSearch_ShouldReturnResults_WhenMatchingContentExists()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "The quick brown fox jumps over the lazy dog.\n\nThis is a second paragraph about cats and dogs.";
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "fox", SearchMode.Hybrid, 10);

        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HybridSearch_ShouldReturnResultsWithFusedScores()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "Machine learning is fascinating.\n\nDeep learning neural networks.";
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "machine learning", SearchMode.Hybrid, 10);

        results.Should().NotBeEmpty();
        // Hybrid scores are RRF fused scores, should be small positive numbers
        results.Should().OnlyContain(r => r.Score > 0);
    }

    [Fact]
    public async Task HybridSearch_ShouldRespectMaxResults()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"Paragraph {i} about search technology."));
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "search technology", SearchMode.Hybrid, 3);

        results.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task HybridSearch_ShouldReturnResultsInDescendingScoreOrder()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "First paragraph about cats.\n\nSecond paragraph about cats and dogs.\n\nThird about birds.";
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "cats", SearchMode.Hybrid, 10);

        results.Should().BeInDescendingOrder(r => r.Score);
    }

    [Fact]
    public async Task HybridSearch_ShouldReturnCorrectMetadata()
    {
        var contentFile = Path.Combine(_testDirectory, "test-doc.txt");
        var content = "A paragraph about quantum computing.";
        await File.WriteAllTextAsync(contentFile, content);

        await _ingester.IngestContentAsync(_indexPath, contentFile);
        var results = await _searchService.SearchAsync(_indexPath, "quantum computing", SearchMode.Hybrid, 10);

        results.Should().NotBeEmpty();
        var result = results[0];
        result.SourceFile.Should().Be("test-doc.txt");
        result.SourceId.Should().Be("test-doc");
        result.Content.Should().Contain("quantum computing");
    }
}
