using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class SearchIndexerTests : IDisposable
{
    private readonly SearchIndexer _indexer;
    private readonly string _testDirectory;
    private readonly string _indexPath;

    public SearchIndexerTests()
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
        _indexPath = Path.Combine(_testDirectory, "test-index");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public async Task IngestContentAsync_ShouldCreateIndex_WhenValidContentProvided()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "This is a test document.\n\nIt has multiple paragraphs for testing search functionality.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);

        Directory.Exists(_indexPath).Should().BeTrue();
        var indexFiles = Directory.GetFiles(_indexPath);
        indexFiles.Should().HaveCountGreaterThan(0);
        indexFiles.Should().Contain(f => f.Contains("segments") || f.Contains(".cfs") || f.Contains(".si"));
    }

    [Fact]
    public async Task IngestContentAsync_ShouldThrowFileNotFoundException_WhenContentFileDoesNotExist()
    {
        var nonExistentFile = Path.Combine(_testDirectory, "nonexistent.txt");

        var act = async () => await _indexer.IngestContentAsync(_indexPath, nonExistentFile);

        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage($"Content file not found: {nonExistentFile}");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnResults_WhenMatchingContentExists()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "The quick brown fox jumps over the lazy dog.\n\nThis is a second paragraph about cats and dogs.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "fox", SearchMode.Bm25, 10);

        results.Should().HaveCount(1);
        results[0].Content.Should().Be("The quick brown fox jumps over the lazy dog.");
        results[0].Score.Should().BeInRange(0.1f, 1.0f);
    }

    [Fact]
    public async Task SearchAsync_ShouldThrowDirectoryNotFoundException_WhenIndexDoesNotExist()
    {
        var nonExistentIndex = Path.Combine(_testDirectory, "nonexistent-index");

        var act = async () => await _indexer.SearchAsync(nonExistentIndex, "test", SearchMode.Bm25, 10, null);

        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage($"Index directory not found: {nonExistentIndex}");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmptyResults_WhenNoMatchingContent()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "The quick brown fox jumps over the lazy dog.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "elephant", SearchMode.Bm25, 10, null);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ShouldRespectMaxResults_WhenLimitingResults()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = string.Join("\n\n", Enumerable.Repeat("This is a test paragraph about dogs.", 10));
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "dogs", SearchMode.Bm25, 3, null);

        results.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterBySourceIds_WhenSourceIdsProvided()
    {
        var contentFile1 = Path.Combine(_testDirectory, "content1.txt");
        var contentFile2 = Path.Combine(_testDirectory, "content2.txt");

        await File.WriteAllTextAsync(contentFile1, "This document mentions cats.");
        await File.WriteAllTextAsync(contentFile2, "This document mentions cats too.");

        await _indexer.IngestContentAsync(_indexPath, contentFile1);
        await _indexer.IngestContentAsync(_indexPath, contentFile2);

        var results = await _indexer.SearchAsync(_indexPath, "cats", SearchMode.Bm25, 10, new[] { "content1" });

        results.Should().HaveCount(1);
        results.Should().OnlyContain(r => r.SourceId == "content1");
        results[0].Content.Should().Be("This document mentions cats.");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnCorrectMetadata_WhenResultsFound()
    {
        var contentFile = Path.Combine(_testDirectory, "test-doc.txt");
        var content = "First paragraph about testing.\n\nSecond paragraph about search functionality.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "testing", SearchMode.Bm25, 10, null);

        results.Should().HaveCount(1);
        var result = results[0];
        result.SourceFile.Should().Be("test-doc.txt");
        result.SourcePath.Should().Be(contentFile);
        result.SourceId.Should().Be("test-doc");
        result.ParagraphNumber.Should().Be(1);
        result.DocumentId.Should().MatchRegex(@"^test-doc\.txt_\d+$");
        result.Content.Should().Be("First paragraph about testing.");
    }

    [Theory]
    [InlineData("quick brown fox", "fox")]
    [InlineData("lazy dog", "dog")]
    [InlineData("cats and dogs", "cats")]
    public async Task SearchAsync_ShouldFindRelevantContent_WhenSearchingVariousTerms(string content, string searchTerm)
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, searchTerm, SearchMode.Bm25, 10, null);

        results.Should().HaveCount(1);
        results[0].Content.Should().Be(content);
        results[0].Score.Should().BeInRange(0.1f, 1.0f);
    }

    [Fact]
    public async Task SearchAsync_ShouldRankBetterMatches_WhenUsingBM25Similarity()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = @"Machine learning is fascinating.

Machine learning algorithms are powerful.

Algorithms can solve complex problems.

Complex problems require innovative solutions.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "machine learning", SearchMode.Bm25, 10, null);

        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results.Should().BeInDescendingOrder(r => r.Score);

        // Results mentioning both terms should score higher than single term matches
        var topResult = results[0];
        topResult.Content.Should().Be("Machine learning is fascinating.");
        topResult.Score.Should().BeInRange(1.0f, 2.0f);
    }

    [Fact]
    public async Task IngestAndQuery_ShouldHandleSpecialCharacters_WhenContentContainsUnicodeAndPunctuation()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "This is a test with special characters: éñ@#$%^&*()!? 测试内容";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "special", SearchMode.Bm25, 10, null);

        results.Should().HaveCount(1);
        results[0].Content.Should().Be("This is a test with special characters: éñ@#$%^&*()!? 测试内容");
    }

    [Fact]
    public async Task IngestAndQuery_ShouldHandleEmptyParagraphs_WhenContentHasWhitespace()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "First paragraph.\n\n\n   \n\nSecond paragraph after empty lines.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "paragraph", SearchMode.Bm25, 10, null);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Content));
        results.Should().Contain(r => r.Content == "First paragraph.");
        results.Should().Contain(r => r.Content == "Second paragraph after empty lines.");
    }

    [Fact]
    public async Task SearchAsync_ShouldUseBM25Scoring_WhenMultipleTermFrequencies()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = @"Document about cats cats cats.

Document about dogs.

Document about cats and dogs together.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.SearchAsync(_indexPath, "cats", SearchMode.Bm25, 10, null);

        results.Should().HaveCount(2);
        results.Should().BeInDescendingOrder(r => r.Score);

        // Document with more mentions of "cats" should score higher due to BM25 term frequency component
        var topResult = results[0];
        topResult.Content.Should().Be("Document about cats cats cats.");
        topResult.Score.Should().BeGreaterThan(results[1].Score);
    }

    [Fact]
    public async Task IngestFolderAsync_ShouldIngestAllSupportedFiles_WhenFolderContainsMultipleFileTypes()
    {
        var folderPath = Path.Combine(_testDirectory, "corpus");
        Directory.CreateDirectory(folderPath);

        // Create test files
        var txtFile1 = Path.Combine(folderPath, "document1.txt");
        var txtFile2 = Path.Combine(folderPath, "document2.txt");
        var mdFile = Path.Combine(folderPath, "notes.md");
        var unsupportedFile = Path.Combine(folderPath, "data.json");

        await File.WriteAllTextAsync(txtFile1, "First text document about artificial intelligence.");
        await File.WriteAllTextAsync(txtFile2, "Second text document about machine learning.");
        await File.WriteAllTextAsync(mdFile, "# Markdown Notes\n\nThis is a markdown document about data science.");
        await File.WriteAllTextAsync(unsupportedFile, "{\"key\": \"value\"}");

        await _indexer.IngestFolderAsync(_indexPath, folderPath);

        // Verify index was created
        Directory.Exists(_indexPath).Should().BeTrue();

        // Search for content from different files
        var aiResults = await _indexer.SearchAsync(_indexPath, "artificial intelligence", SearchMode.Bm25, 10, null);
        var mlResults = await _indexer.SearchAsync(_indexPath, "machine learning", SearchMode.Bm25, 10, null);
        var dsResults = await _indexer.SearchAsync(_indexPath, "data science", SearchMode.Bm25, 10, null);

        // Should find content from .txt files
        aiResults.Should().HaveCountGreaterThan(0);
        mlResults.Should().HaveCountGreaterThan(0);

        // Should find content from .md file
        dsResults.Should().HaveCountGreaterThan(0);

        // Should not find content from unsupported .json file
        var jsonResults = await _indexer.SearchAsync(_indexPath, "key", SearchMode.Bm25, 10, null);
        jsonResults.Should().BeEmpty();
    }

    [Fact]
    public async Task IngestFolderAsync_ShouldHandleNestedDirectories_WhenRecursiveSearchEnabled()
    {
        var folderPath = Path.Combine(_testDirectory, "corpus");
        var subFolder = Path.Combine(folderPath, "subfolder");
        Directory.CreateDirectory(subFolder);

        var rootFile = Path.Combine(folderPath, "root.txt");
        var nestedFile = Path.Combine(subFolder, "nested.txt");

        await File.WriteAllTextAsync(rootFile, "Root level document about quantum computing.");
        await File.WriteAllTextAsync(nestedFile, "Nested document about blockchain technology.");

        await _indexer.IngestFolderAsync(_indexPath, folderPath);

        // Should find both root and nested files
        var quantumResults = await _indexer.SearchAsync(_indexPath, "quantum computing", SearchMode.Bm25, 10, null);
        var blockchainResults = await _indexer.SearchAsync(_indexPath, "blockchain", SearchMode.Bm25, 10, null);

        quantumResults.Should().HaveCountGreaterThan(0);
        blockchainResults.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task IngestFolderAsync_ShouldThrowDirectoryNotFoundException_WhenFolderDoesNotExist()
    {
        var nonExistentFolder = Path.Combine(_testDirectory, "nonexistent-folder");

        var act = async () => await _indexer.IngestFolderAsync(_indexPath, nonExistentFolder);

        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage($"Folder not found: {nonExistentFolder}");
    }

    [Fact]
    public async Task IngestFolderAsync_ShouldHandleEmptyFolder_WhenNoSupportedFiles()
    {
        var emptyFolder = Path.Combine(_testDirectory, "empty-folder");
        Directory.CreateDirectory(emptyFolder);

        // Should not throw, just handle gracefully
        await _indexer.IngestFolderAsync(_indexPath, emptyFolder);

        // Index should not be created if no files were ingested
        // Or if created, should be empty
        if (Directory.Exists(_indexPath))
        {
            var results = await _indexer.SearchAsync(_indexPath, "anything", SearchMode.Bm25, 10, null);
            results.Should().BeEmpty();
        }
    }
}
