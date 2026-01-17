using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class SearchIndexerTests : IDisposable
{
    private readonly SearchIndexer _indexer;
    private readonly string _testDirectory;
    private readonly string _indexPath;

    public SearchIndexerTests()
    {
        _indexer = new SearchIndexer(new MockEmbeddingService());
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
    public async Task QueryAsync_ShouldReturnResults_WhenMatchingContentExists()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "The quick brown fox jumps over the lazy dog.\n\nThis is a second paragraph about cats and dogs.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.QueryAsync(_indexPath, "fox", 10);

        results.Should().HaveCount(1);
        results[0].Content.Should().Be("The quick brown fox jumps over the lazy dog.");
        results[0].Score.Should().BeInRange(0.1f, 1.0f);
    }

    [Fact]
    public async Task QueryAsync_ShouldThrowDirectoryNotFoundException_WhenIndexDoesNotExist()
    {
        var nonExistentIndex = Path.Combine(_testDirectory, "nonexistent-index");

        var act = async () => await _indexer.QueryAsync(nonExistentIndex, "test", 10);

        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage($"Index directory not found: {nonExistentIndex}");
    }

    [Fact]
    public async Task QueryAsync_ShouldReturnEmptyResults_WhenNoMatchingContent()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = "The quick brown fox jumps over the lazy dog.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.QueryAsync(_indexPath, "elephant", 10);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_ShouldRespectMaxResults_WhenLimitingResults()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = string.Join("\n\n", Enumerable.Repeat("This is a test paragraph about dogs.", 10));
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.QueryAsync(_indexPath, "dogs", 3);

        results.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task QueryAsync_ShouldFilterBySourceIds_WhenSourceIdsProvided()
    {
        var contentFile1 = Path.Combine(_testDirectory, "content1.txt");
        var contentFile2 = Path.Combine(_testDirectory, "content2.txt");

        await File.WriteAllTextAsync(contentFile1, "This document mentions cats.");
        await File.WriteAllTextAsync(contentFile2, "This document mentions cats too.");

        await _indexer.IngestContentAsync(_indexPath, contentFile1);
        await _indexer.IngestContentAsync(_indexPath, contentFile2);

        var results = await _indexer.QueryAsync(_indexPath, "cats", 10, new[] { "content1" });

        results.Should().HaveCount(1);
        results.Should().OnlyContain(r => r.SourceId == "content1");
        results[0].Content.Should().Be("This document mentions cats.");
    }

    [Fact]
    public async Task QueryAsync_ShouldReturnCorrectMetadata_WhenResultsFound()
    {
        var contentFile = Path.Combine(_testDirectory, "test-doc.txt");
        var content = "First paragraph about testing.\n\nSecond paragraph about search functionality.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.QueryAsync(_indexPath, "testing", 10);

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
    public async Task QueryAsync_ShouldFindRelevantContent_WhenSearchingVariousTerms(string content, string searchTerm)
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.QueryAsync(_indexPath, searchTerm, 10);

        results.Should().HaveCount(1);
        results[0].Content.Should().Be(content);
        results[0].Score.Should().BeInRange(0.1f, 1.0f);
    }

    [Fact]
    public async Task QueryAsync_ShouldRankBetterMatches_WhenUsingBM25Similarity()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = @"Machine learning is fascinating.

Machine learning algorithms are powerful.

Algorithms can solve complex problems.

Complex problems require innovative solutions.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.QueryAsync(_indexPath, "machine learning", 10);

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
        var results = await _indexer.QueryAsync(_indexPath, "special", 10);

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
        var results = await _indexer.QueryAsync(_indexPath, "paragraph", 10);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Content));
        results.Should().Contain(r => r.Content == "First paragraph.");
        results.Should().Contain(r => r.Content == "Second paragraph after empty lines.");
    }

    [Fact]
    public async Task QueryAsync_ShouldUseBM25Scoring_WhenMultipleTermFrequencies()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        var content = @"Document about cats cats cats.

Document about dogs.

Document about cats and dogs together.";
        await File.WriteAllTextAsync(contentFile, content);

        await _indexer.IngestContentAsync(_indexPath, contentFile);
        var results = await _indexer.QueryAsync(_indexPath, "cats", 10);

        results.Should().HaveCount(2);
        results.Should().BeInDescendingOrder(r => r.Score);

        // Document with more mentions of "cats" should score higher due to BM25 term frequency component
        var topResult = results[0];
        topResult.Content.Should().Be("Document about cats cats cats.");
        topResult.Score.Should().BeGreaterThan(results[1].Score);
    }
}
