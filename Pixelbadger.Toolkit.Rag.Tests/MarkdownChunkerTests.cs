using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class MarkdownChunkerTests
{
    [Fact]
    public void ChunkByHeaders_ShouldReturnEmpty_WhenContentIsNull()
    {
        var result = MarkdownChunker.ChunkByHeaders(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByHeaders_ShouldReturnEmpty_WhenContentIsEmpty()
    {
        var result = MarkdownChunker.ChunkByHeaders(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByHeaders_ShouldReturnEmpty_WhenContentIsWhitespace()
    {
        var result = MarkdownChunker.ChunkByHeaders("   \n  \n  ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByHeaders_ShouldSplitOnHeaders()
    {
        var content = "# Header 1\nContent under header 1.\n# Header 2\nContent under header 2.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(2);
        result[0].HeaderText.Should().Be("Header 1");
        result[0].Content.Should().Contain("Content under header 1.");
        result[1].HeaderText.Should().Be("Header 2");
        result[1].Content.Should().Contain("Content under header 2.");
    }

    [Fact]
    public void ChunkByHeaders_ShouldDetectHeaderLevel()
    {
        var content = "# H1\nContent\n## H2\nContent\n### H3\nContent";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(3);
        result[0].HeaderLevel.Should().Be(1);
        result[1].HeaderLevel.Should().Be(2);
        result[2].HeaderLevel.Should().Be(3);
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandlePreHeaderContent()
    {
        var content = "Some intro text before any header.\n# First Header\nHeader content.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(2);
        result[0].Content.Should().Be("Some intro text before any header.");
        result[0].HeaderText.Should().BeEmpty();
        result[0].HeaderLevel.Should().Be(0);
        result[1].HeaderText.Should().Be("First Header");
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleContentWithNoHeaders()
    {
        var content = "Just plain text.\nWith multiple lines.\nBut no headers.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("Just plain text.\nWith multiple lines.\nBut no headers.");
        result[0].HeaderText.Should().BeEmpty();
        result[0].HeaderLevel.Should().Be(0);
    }

    [Fact]
    public void ChunkByHeaders_ShouldIncludeHeaderInChunkContent()
    {
        var content = "# My Header\nBody text.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(1);
        result[0].Content.Should().StartWith("# My Header");
        result[0].Content.Should().Contain("Body text.");
    }

    [Fact]
    public void ChunkByHeaders_ShouldTrackLineNumbers()
    {
        var content = "# Header 1\nLine 2\nLine 3\n# Header 2\nLine 5";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(2);
        result[0].StartLine.Should().Be(1);
        result[0].EndLine.Should().Be(3);
        result[1].StartLine.Should().Be(4);
        result[1].EndLine.Should().Be(5);
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleH6Headers()
    {
        var content = "###### Deep Header\nContent.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(1);
        result[0].HeaderLevel.Should().Be(6);
        result[0].HeaderText.Should().Be("Deep Header");
    }

    [Fact]
    public void ChunkByHeaders_ShouldNotTreatSevenHashesAsHeader()
    {
        var content = "####### Not a header\nContent.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(1);
        result[0].HeaderLevel.Should().Be(0);
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleEmptyChunksBetweenHeaders()
    {
        var content = "# Header 1\n# Header 2\nContent.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        // Header 1 has no content, should still appear if header line itself is content
        // The chunk includes the header line, so "# Header 1" is non-empty
        result.Should().HaveCount(2);
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleWindowsLineEndings()
    {
        var content = "# Header 1\r\nContent line 1.\r\n# Header 2\r\nContent line 2.";

        var result = MarkdownChunker.ChunkByHeaders(content);

        result.Should().HaveCount(2);
        result[0].HeaderText.Should().Be("Header 1");
        result[1].HeaderText.Should().Be("Header 2");
    }
}
