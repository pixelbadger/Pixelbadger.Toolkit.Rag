using FluentAssertions;
using Microsoft.Extensions.AI;
using Pixelbadger.Toolkit.Rag.Components;
using SemanticChunkerNET;
using Xunit;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class SemanticChunkerTests
{
    [Fact]
    public void SemanticTextChunker_ShouldThrowException_WhenApiKeyNotProvided()
    {
        // Arrange - Clear environment variable if set
        var originalKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        try
        {
            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                var chunker = new SemanticTextChunker();
            });

            exception.Message.Should().Contain("OpenAI API key must be provided");
        }
        finally
        {
            // Restore original environment variable
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalKey);
        }
    }

    [Fact]
    public void SemanticTextChunker_ShouldImplementITextChunker()
    {
        // Arrange & Act
        var chunker = new SemanticTextChunker("test-key");

        // Assert
        chunker.Should().BeAssignableTo<ITextChunker>();
    }

    [Fact]
    public void SemanticChunkWrapper_ShouldImplementIChunk()
    {
        // Arrange
        var chunk = new Chunk("chunk-id", "Test content", new Embedding<float>(new float[] { 0.1f, 0.2f }));

        // Act
        var wrapper = new SemanticChunkWrapper(chunk, 1);

        // Assert
        wrapper.Should().BeAssignableTo<IChunk>();
        wrapper.Content.Should().Be("Test content");
        wrapper.ChunkNumber.Should().Be(1);
    }

    [Fact]
    public async Task SemanticTextChunker_ShouldCreateChunks_WhenContentProvided()
    {
        // Arrange
        var content = "First sentence. Second sentence. Third sentence.";
        var mockGenerator = new MockEmbeddingGenerator();
        var chunker = new TestableSemanticTextChunker(mockGenerator);

        // Act
        var chunks = await chunker.ChunkTextAsync(content);

        // Assert
        chunks.Should().NotBeEmpty();
        chunks.Should().HaveCountGreaterThan(0);

        // Verify all chunks have content
        foreach (var chunk in chunks)
        {
            chunk.Content.Should().NotBeNullOrWhiteSpace();
            chunk.ChunkNumber.Should().BeGreaterThan(0);
        }

        // Verify chunk numbers are sequential
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].ChunkNumber.Should().Be(i + 1);
        }
    }

    [Fact]
    public async Task SemanticTextChunker_ShouldHandleEmptyContent()
    {
        // Arrange
        var content = "";
        var mockGenerator = new MockEmbeddingGenerator();
        var chunker = new TestableSemanticTextChunker(mockGenerator);

        // Act
        var chunks = await chunker.ChunkTextAsync(content);

        // Assert
        chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task SemanticTextChunker_ShouldHandleSingleSentence()
    {
        // Arrange
        var content = "This is a single sentence.";
        var mockGenerator = new MockEmbeddingGenerator();
        var chunker = new TestableSemanticTextChunker(mockGenerator);

        // Act
        var chunks = await chunker.ChunkTextAsync(content);

        // Assert
        chunks.Should().HaveCount(1);
        chunks[0].Content.Should().Be(content);
        chunks[0].ChunkNumber.Should().Be(1);
    }

    /// <summary>
    /// Testable version of SemanticTextChunker that accepts a mock embedding generator
    /// </summary>
    private class TestableSemanticTextChunker : ITextChunker
    {
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly int _tokenLimit;
        private readonly int _bufferSize;
        private readonly ThresholdType _thresholdType;
        private readonly double _thresholdAmount;

        public TestableSemanticTextChunker(
            IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
            int tokenLimit = 512,
            int bufferSize = 1,
            ThresholdType thresholdType = ThresholdType.Percentile,
            double thresholdAmount = 95)
        {
            _embeddingGenerator = embeddingGenerator;
            _tokenLimit = tokenLimit;
            _bufferSize = bufferSize;
            _thresholdType = thresholdType;
            _thresholdAmount = thresholdAmount;
        }

        public async Task<List<IChunk>> ChunkTextAsync(string content)
        {
            var semanticChunker = new SemanticChunker(
                _embeddingGenerator,
                tokenLimit: _tokenLimit,
                bufferSize: _bufferSize,
                thresholdType: _thresholdType,
                thresholdAmount: _thresholdAmount
            );

            var chunks = await semanticChunker.CreateChunksAsync(content);

            return chunks.Select((chunk, index) => (IChunk)new SemanticChunkWrapper(chunk, index + 1)).ToList();
        }
    }
}
