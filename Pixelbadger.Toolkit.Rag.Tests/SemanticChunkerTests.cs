using FluentAssertions;
using Microsoft.Extensions.AI;
using Pixelbadger.Toolkit.Rag.Components;
using SemanticChunkerNET;
using Xunit;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class SemanticChunkerTests
{
    [Fact]
    public void SemanticTextChunker_ShouldThrowException_WhenEmbeddingGeneratorIsNull()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            var chunker = new SemanticTextChunker(null!);
        });

        exception.ParamName.Should().Be("embeddingGenerator");
    }

    [Fact]
    public void SemanticTextChunker_ShouldImplementITextChunker()
    {
        // Arrange
        var mockGenerator = new MockEmbeddingGenerator();

        // Act
        var chunker = new SemanticTextChunker(mockGenerator);

        // Assert
        chunker.Should().BeAssignableTo<ITextChunker>();
    }

    [Fact]
    public void SemanticChunkWrapper_ShouldImplementIChunk()
    {
        // Arrange
        var chunk = new Chunk { Id = "chunk-id", Text = "Test content", Embedding = new Embedding<float>(new float[] { 0.1f, 0.2f }) };

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
        var chunker = new SemanticTextChunker(mockGenerator);

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
        var chunker = new SemanticTextChunker(mockGenerator);

        // Act
        var chunks = await chunker.ChunkTextAsync(content);

        // Assert - SemanticChunker.NET 1.1.0 returns a single chunk with empty content
        chunks.Should().HaveCount(1);
        chunks[0].Content.Should().BeEmpty();
    }

    [Fact]
    public async Task SemanticTextChunker_ShouldHandleSingleSentence()
    {
        // Arrange
        var content = "This is a single sentence.";
        var mockGenerator = new MockEmbeddingGenerator();
        var chunker = new SemanticTextChunker(mockGenerator);

        // Act
        var chunks = await chunker.ChunkTextAsync(content);

        // Assert
        chunks.Should().HaveCount(1);
        chunks[0].Content.Should().Be(content);
        chunks[0].ChunkNumber.Should().Be(1);
    }

}
