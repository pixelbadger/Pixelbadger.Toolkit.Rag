using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class FileReaderFactoryTests
{
    private readonly FileReaderFactory _factory;

    public FileReaderFactoryTests()
    {
        var readers = new List<IFileReader>
        {
            new PlainTextFileReader(),
            new MarkdownFileReader()
        };
        _factory = new FileReaderFactory(readers);
    }

    [Fact]
    public void GetReader_ShouldReturnPlainTextReader_ForTxtFiles()
    {
        var reader = _factory.GetReader("document.txt");

        reader.Should().BeOfType<PlainTextFileReader>();
    }

    [Fact]
    public void GetReader_ShouldReturnMarkdownReader_ForMdFiles()
    {
        var reader = _factory.GetReader("document.md");

        reader.Should().BeOfType<MarkdownFileReader>();
    }

    [Fact]
    public void GetReader_ShouldBeCaseInsensitive()
    {
        var reader = _factory.GetReader("document.TXT");

        reader.Should().BeOfType<PlainTextFileReader>();
    }

    [Fact]
    public void GetReader_ShouldThrowNotSupportedException_ForUnsupportedExtension()
    {
        var act = () => _factory.GetReader("document.json");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*No file reader available*");
    }

    [Fact]
    public void GetReader_ShouldThrowNotSupportedException_ForNoExtension()
    {
        var act = () => _factory.GetReader("README");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*File has no extension*");
    }

    [Fact]
    public void CanRead_ShouldReturnTrue_ForSupportedExtensions()
    {
        _factory.CanRead("file.txt").Should().BeTrue();
        _factory.CanRead("file.md").Should().BeTrue();
    }

    [Fact]
    public void CanRead_ShouldReturnFalse_ForUnsupportedExtensions()
    {
        _factory.CanRead("file.json").Should().BeFalse();
        _factory.CanRead("file.csv").Should().BeFalse();
    }

    [Fact]
    public void CanRead_ShouldReturnFalse_ForNoExtension()
    {
        _factory.CanRead("Makefile").Should().BeFalse();
    }

    [Fact]
    public void SupportedExtensions_ShouldContainTxtAndMd()
    {
        _factory.SupportedExtensions.Should().Contain(".txt");
        _factory.SupportedExtensions.Should().Contain(".md");
    }
}
