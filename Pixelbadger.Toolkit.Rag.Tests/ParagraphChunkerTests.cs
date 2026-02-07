using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class ParagraphChunkerTests
{
    [Fact]
    public void ChunkByParagraphs_ShouldReturnEmpty_WhenContentIsNull()
    {
        var result = ParagraphChunker.ChunkByParagraphs(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByParagraphs_ShouldReturnEmpty_WhenContentIsEmpty()
    {
        var result = ParagraphChunker.ChunkByParagraphs(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByParagraphs_ShouldReturnEmpty_WhenContentIsWhitespace()
    {
        var result = ParagraphChunker.ChunkByParagraphs("   \n  \n  ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByParagraphs_ShouldSplitOnDoubleNewlines()
    {
        var content = "First paragraph.\n\nSecond paragraph.\n\nThird paragraph.";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result.Should().HaveCount(3);
        result[0].Content.Should().Be("First paragraph.");
        result[1].Content.Should().Be("Second paragraph.");
        result[2].Content.Should().Be("Third paragraph.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldSplitOnWindowsDoubleNewlines()
    {
        var content = "First paragraph.\r\n\r\nSecond paragraph.";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result.Should().HaveCount(2);
        result[0].Content.Should().Be("First paragraph.");
        result[1].Content.Should().Be("Second paragraph.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldFallBackToSingleNewlines_WhenNoDoubleNewlines()
    {
        var content = "Line one.\nLine two.\nLine three.";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result.Should().HaveCount(3);
        result[0].Content.Should().Be("Line one.");
        result[1].Content.Should().Be("Line two.");
        result[2].Content.Should().Be("Line three.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldNotFallBackToSingleNewlines_WhenDoubleNewlinesExist()
    {
        var content = "Paragraph one line one.\nParagraph one line two.\n\nParagraph two.";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result.Should().HaveCount(2);
        result[0].Content.Should().Be("Paragraph one line one.\nParagraph one line two.");
        result[1].Content.Should().Be("Paragraph two.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldAssignSequentialChunkNumbers()
    {
        var content = "A.\n\nB.\n\nC.";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result[0].ChunkNumber.Should().Be(1);
        result[1].ChunkNumber.Should().Be(2);
        result[2].ChunkNumber.Should().Be(3);
    }

    [Fact]
    public void ChunkByParagraphs_ShouldFilterOutWhitespaceOnlyParagraphs()
    {
        var content = "Content.\n\n   \n\nMore content.";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result.Should().HaveCount(2);
        result[0].Content.Should().Be("Content.");
        result[1].Content.Should().Be("More content.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldTrimWhitespace()
    {
        var content = "  First paragraph.  \n\n  Second paragraph.  ";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result[0].Content.Should().Be("First paragraph.");
        result[1].Content.Should().Be("Second paragraph.");
    }

    [Fact]
    public void ChunkByParagraphs_ShouldReturnSingleChunk_WhenNoParagraphSeparators()
    {
        var content = "Single block of text with no newlines at all.";

        var result = ParagraphChunker.ChunkByParagraphs(content);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("Single block of text with no newlines at all.");
        result[0].ChunkNumber.Should().Be(1);
    }
}
