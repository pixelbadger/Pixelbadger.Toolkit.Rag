using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class ParagraphChunkerTests
{
    [Fact]
    public void ChunkByParagraphs_ShouldReturnEmptyList_ForNullOrWhitespaceContent()
    {
        // Arrange & Act
        var result1 = ParagraphChunker.ChunkByParagraphs(null!);
        var result2 = ParagraphChunker.ChunkByParagraphs("");
        var result3 = ParagraphChunker.ChunkByParagraphs("   ");

        // Assert
        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
        result3.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByParagraphs_ShouldSplitByDoubleNewlines()
    {
        // Arrange
        var content = "First paragraph.\n\nSecond paragraph.\n\nThird paragraph.";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(3);
        result[0].Content.Should().Be("First paragraph.");
        result[0].ChunkNumber.Should().Be(1);
        result[1].Content.Should().Be("Second paragraph.");
        result[1].ChunkNumber.Should().Be(2);
        result[2].Content.Should().Be("Third paragraph.");
        result[2].ChunkNumber.Should().Be(3);
    }

    [Fact]
    public void ChunkByParagraphs_ShouldHandleDifferentNewlineFormats()
    {
        // Arrange
        var content1 = "First paragraph.\r\n\r\nSecond paragraph.";
        var content2 = "First paragraph.\r\rSecond paragraph.";

        // Act
        var result1 = ParagraphChunker.ChunkByParagraphs(content1);
        var result2 = ParagraphChunker.ChunkByParagraphs(content2);

        // Assert
        result1.Should().HaveCount(2);
        result1[0].Content.Should().Be("First paragraph.");
        result1[1].Content.Should().Be("Second paragraph.");

        result2.Should().HaveCount(2);
        result2[0].Content.Should().Be("First paragraph.");
        result2[1].Content.Should().Be("Second paragraph.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldFallbackToSingleNewlines_WhenNoDoubleNewlines()
    {
        // Arrange
        var content = "First line.\nSecond line.\nThird line.";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(3);
        result[0].Content.Should().Be("First line.");
        result[1].Content.Should().Be("Second line.");
        result[2].Content.Should().Be("Third line.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldTrimWhitespace()
    {
        // Arrange
        var content = "  First paragraph.  \n\n  Second paragraph.  ";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(2);
        result[0].Content.Should().Be("First paragraph.");
        result[1].Content.Should().Be("Second paragraph.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldSkipEmptyParagraphs()
    {
        // Arrange
        var content = "First paragraph.\n\n\n\nSecond paragraph.\n\n   \n\nThird paragraph.";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(3);
        result[0].Content.Should().Be("First paragraph.");
        result[1].Content.Should().Be("Second paragraph.");
        result[2].Content.Should().Be("Third paragraph.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldHandleSingleParagraph()
    {
        // Arrange
        var content = "This is a single paragraph with no newlines.";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(1);
        result[0].Content.Should().Be("This is a single paragraph with no newlines.");
        result[0].ChunkNumber.Should().Be(1);
    }

    [Fact]
    public void ChunkByParagraphs_ShouldHandleMultilineContent()
    {
        // Arrange
        var content = @"This is the first paragraph.
It spans multiple lines.

This is the second paragraph.
It also spans multiple lines.
And has three lines total.";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(2);
        result[0].Content.Should().Be("This is the first paragraph.\nIt spans multiple lines.");
        result[1].Content.Should().Be("This is the second paragraph.\nIt also spans multiple lines.\nAnd has three lines total.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldAssignCorrectChunkNumbers()
    {
        // Arrange
        var content = "Para 1\n\nPara 2\n\nPara 3\n\nPara 4\n\nPara 5";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(5);
        for (int i = 0; i < result.Count; i++)
        {
            result[i].ChunkNumber.Should().Be(i + 1);
        }
    }

    [Fact]
    public void ChunkByParagraphs_ShouldPreserveParagraphContent()
    {
        // Arrange
        var content = "Paragraph with special characters: @#$%^&*()!\n\nParagraph with numbers: 123456789\n\nParagraph with unicode: 你好世界";

        // Act
        var result = ParagraphChunker.ChunkByParagraphs(content);

        // Assert
        result.Should().HaveCount(3);
        result[0].Content.Should().Be("Paragraph with special characters: @#$%^&*()!");
        result[1].Content.Should().Be("Paragraph with numbers: 123456789");
        result[2].Content.Should().Be("Paragraph with unicode: 你好世界");
    }
}
