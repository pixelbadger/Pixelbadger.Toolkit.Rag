using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class ChunkerFactoryTests
{
    private readonly ChunkerFactory _factory;

    public ChunkerFactoryTests()
    {
        var chunkers = new List<ITextChunker>
        {
            new MarkdownTextChunker(),
            new ParagraphTextChunker()
        };
        _factory = new ChunkerFactory(chunkers);
    }

    [Fact]
    public void GetChunker_ShouldReturnMarkdownChunker_ForMdFiles()
    {
        var chunker = _factory.GetChunker("document.md");

        chunker.Should().BeOfType<MarkdownTextChunker>();
    }

    [Fact]
    public void GetChunker_ShouldReturnMarkdownChunker_ForMdFilesCaseInsensitive()
    {
        var chunker = _factory.GetChunker("document.MD");

        chunker.Should().BeOfType<MarkdownTextChunker>();
    }

    [Fact]
    public void GetChunker_ShouldReturnParagraphChunker_ForTxtFiles()
    {
        var chunker = _factory.GetChunker("document.txt");

        chunker.Should().BeOfType<ParagraphTextChunker>();
    }

    [Fact]
    public void GetChunker_ShouldReturnParagraphChunker_ForUnknownExtensions()
    {
        var chunker = _factory.GetChunker("document.json");

        chunker.Should().BeOfType<ParagraphTextChunker>();
    }

    [Fact]
    public void GetChunker_ShouldReturnParagraphChunker_ForFilesWithNoExtension()
    {
        var chunker = _factory.GetChunker("README");

        chunker.Should().BeOfType<ParagraphTextChunker>();
    }

    [Fact]
    public void GetChunker_ShouldReturnParagraphChunker_ForPathWithDirectories()
    {
        var chunker = _factory.GetChunker("/some/path/to/file.txt");

        chunker.Should().BeOfType<ParagraphTextChunker>();
    }

    [Fact]
    public void GetChunker_ShouldReturnMarkdownChunker_ForPathWithDirectories()
    {
        var chunker = _factory.GetChunker("/some/path/to/file.md");

        chunker.Should().BeOfType<MarkdownTextChunker>();
    }
}
