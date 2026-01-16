using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class MarkdownChunkerTests
{
    [Fact]
    public void ChunkByHeaders_ShouldReturnEmptyList_ForNullOrWhitespaceContent()
    {
        // Arrange & Act
        var result1 = MarkdownChunker.ChunkByHeaders(null!);
        var result2 = MarkdownChunker.ChunkByHeaders("");
        var result3 = MarkdownChunker.ChunkByHeaders("   ");

        // Assert
        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
        result3.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleContentWithoutHeaders()
    {
        // Arrange
        var content = "This is just plain text.\nWith multiple lines.\nBut no headers.";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(1);
        result[0].Content.Should().Be("This is just plain text.\nWith multiple lines.\nBut no headers.");
        result[0].HeaderText.Should().Be("");
        result[0].HeaderLevel.Should().Be(0);
        result[0].StartLine.Should().Be(1);
        result[0].EndLine.Should().Be(3);
    }

    [Fact]
    public void ChunkByHeaders_ShouldChunkByH1Headers()
    {
        // Arrange
        var content = @"# First Section
This is content under first section.

# Second Section
This is content under second section.
With multiple lines.";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(2);

        result[0].HeaderText.Should().Be("First Section");
        result[0].HeaderLevel.Should().Be(1);
        result[0].Content.Should().Contain("# First Section");
        result[0].Content.Should().Contain("This is content under first section.");

        result[1].HeaderText.Should().Be("Second Section");
        result[1].HeaderLevel.Should().Be(1);
        result[1].Content.Should().Contain("# Second Section");
        result[1].Content.Should().Contain("This is content under second section.");
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleDifferentHeaderLevels()
    {
        // Arrange
        var content = @"# Main Header
Main content.

## Sub Header
Sub content.

### Sub Sub Header
Sub sub content.

#### Level 4 Header
Level 4 content.";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(4);
        result[0].HeaderLevel.Should().Be(1);
        result[1].HeaderLevel.Should().Be(2);
        result[2].HeaderLevel.Should().Be(3);
        result[3].HeaderLevel.Should().Be(4);
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleContentBeforeFirstHeader()
    {
        // Arrange
        var content = @"This is intro content.
Without a header.

# First Header
Content under first header.";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(2);

        result[0].HeaderText.Should().Be("");
        result[0].HeaderLevel.Should().Be(0);
        result[0].Content.Should().Be("This is intro content.\nWithout a header.");
        result[0].StartLine.Should().Be(1);

        result[1].HeaderText.Should().Be("First Header");
        result[1].HeaderLevel.Should().Be(1);
    }

    [Fact]
    public void ChunkByHeaders_ShouldCalculateLineNumbersCorrectly()
    {
        // Arrange
        var content = @"Line 1
Line 2
# Header 1
Line 4
Line 5
## Header 2
Line 7
Line 8
Line 9";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(3);

        // Pre-header content
        result[0].StartLine.Should().Be(1);
        result[0].EndLine.Should().Be(2);

        // First header section
        result[1].StartLine.Should().Be(3);
        result[1].EndLine.Should().Be(5);

        // Second header section
        result[2].StartLine.Should().Be(6);
        result[2].EndLine.Should().Be(9);
    }

    [Fact]
    public void ChunkByHeaders_ShouldExtractHeaderTextCorrectly()
    {
        // Arrange
        var content = @"# Header with Special Characters!@#$%
Content 1

## Header with Numbers 123
Content 2

### Header with   Extra   Spaces
Content 3";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(3);
        result[0].HeaderText.Should().Be("Header with Special Characters!@#$%");
        result[1].HeaderText.Should().Be("Header with Numbers 123");
        result[2].HeaderText.Should().Be("Header with   Extra   Spaces");
    }

    [Fact]
    public void ChunkByHeaders_ShouldIgnoreInvalidHeaders()
    {
        // Arrange
        var content = @"#Not a header (no space)
####### Too many hashes
# Valid Header
Content here.";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(2);

        // Pre-header content with invalid headers
        result[0].HeaderText.Should().Be("");
        result[0].Content.Should().Contain("#Not a header");
        result[0].Content.Should().Contain("####### Too many hashes");

        // Valid header
        result[1].HeaderText.Should().Be("Valid Header");
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleEmptyLines()
    {
        // Arrange
        var content = @"# Header 1


Content with empty lines above.


# Header 2

Content with empty lines.

";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(2);
        result[0].HeaderText.Should().Be("Header 1");
        result[1].HeaderText.Should().Be("Header 2");

        // Should preserve empty lines in content
        result[0].Content.Should().Contain("\n\n");
        result[1].Content.Should().Contain("\n\n");
    }

    [Fact]
    public void ChunkByHeaders_ShouldTrimWhitespaceFromChunks()
    {
        // Arrange
        var content = @"
# Header 1
   Content with whitespace

# Header 2
   More content
   ";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(2);
        result[0].Content.Should().NotStartWith(" ");
        result[0].Content.Should().NotEndWith(" ");
        result[1].Content.Should().NotStartWith(" ");
        result[1].Content.Should().NotEndWith(" ");
    }

    [Fact]
    public void ChunkByHeaders_ShouldHandleComplexMarkdownDocument()
    {
        // Arrange
        var content = @"Introduction paragraph before any headers.

# Getting Started
This section covers the basics.

Some code example:
```
code block here
```

## Installation
How to install the software.

### Prerequisites
What you need first.

# Advanced Topics
More complex material.

## Configuration
Setting things up.";

        // Act
        var result = MarkdownChunker.ChunkByHeaders(content);

        // Assert
        result.Should().HaveCount(6);
        result[0].HeaderText.Should().Be(""); // Intro
        result[1].HeaderText.Should().Be("Getting Started");
        result[2].HeaderText.Should().Be("Installation");
        result[3].HeaderText.Should().Be("Prerequisites");
        result[4].HeaderText.Should().Be("Advanced Topics");
        result[5].HeaderText.Should().Be("Configuration");
    }
}
