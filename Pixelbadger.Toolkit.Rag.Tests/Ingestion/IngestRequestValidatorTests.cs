using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Ingestion;

namespace Pixelbadger.Toolkit.Rag.Tests.Ingestion;

public class IngestRequestValidatorTests
{
    private static IngestRequestValidator Validator(IngestSettings? settings = null) => new(
        settings ?? new IngestSettings(),
        new FileReaderFactory([new PlainTextFileReader(), new MarkdownFileReader()]));

    private static IngestValidationResult Validate(string fileName, long length = 1, IngestSettings? settings = null, string? maxChunk = null) =>
        Validator(settings).Validate([new IngestFileCandidate(fileName, length)], maxChunk);

    [Theory]
    [InlineData("a.md", "a.md")]
    [InlineData("docs/a.md", "docs/a.md")]
    [InlineData(@"docs\sub\a.TXT", "docs/sub/a.TXT")]
    [InlineData("deep/er/path/pic.PNG", "deep/er/path/pic.PNG")]
    [InlineData("my file (1).mp3", "my file (1).mp3")]
    [InlineData("..hidden/a.md", "..hidden/a.md")]
    public void SafePaths_AreAcceptedAndNormalised(string raw, string expected)
    {
        var result = Validate(raw);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.SelectMany(e => e.Value)));
        result.Paths.Should().Equal(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../a.md")]
    [InlineData("a/../b.md")]
    [InlineData("a/./b.md")]
    [InlineData("/a.md")]
    [InlineData(@"\a.md")]
    [InlineData("C:/a.md")]
    [InlineData(@"d:\a.md")]
    [InlineData("a//b.md")]
    [InlineData("a/")]
    [InlineData("a/ /b.md")]
    [InlineData("a\0b.md")]
    [InlineData("a\nb.md")]
    public void UnsafePaths_AreRejected(string raw)
    {
        Validate(raw).IsValid.Should().BeFalse();
        IngestRequestValidator.TryNormalizePath(raw, out _, out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void PathsLongerThan1024Characters_AreRejected()
    {
        Validate(new string('a', 1030) + ".md").IsValid.Should().BeFalse();
        Validate(new string('a', 1000) + ".md").IsValid.Should().BeTrue();
    }

    [Fact]
    public void FileSizeLimit_IsInclusive()
    {
        var settings = new IngestSettings { MaxFileSizeBytes = 10 };

        Validate("a.txt", 10, settings).IsValid.Should().BeTrue();
        var over = Validate("a.txt", 11, settings);
        over.IsValid.Should().BeFalse();
        over.Errors["files"].Single().Should().Contain("'a.txt'").And.Contain("exceeds the limit of 10 bytes");
    }

    [Fact]
    public void FileCountLimit_IsInclusive_AndAtLeastOneFileIsRequired()
    {
        var settings = new IngestSettings { MaxFilesPerJob = 2 };
        var validator = Validator(settings);
        IngestFileCandidate F(string n) => new(n, 1);

        validator.Validate([F("a.txt"), F("b.txt")], null).IsValid.Should().BeTrue();
        validator.Validate([F("a.txt"), F("b.txt"), F("c.txt")], null).Errors["files"].Single().Should().Contain("limit of 2");
        validator.Validate([], null).Errors["files"].Single().Should().Contain("At least one file");
    }

    [Fact]
    public void DuplicatePaths_AreRejected_AfterNormalisation()
    {
        var result = Validator().Validate([new("docs/a.md", 1), new(@"docs\a.md", 1)], null);

        result.Errors["files"].Should().ContainSingle(e => e.Contains("duplicate"));
    }

    [Fact]
    public void PathsDifferingOnlyByCase_AreDifferentDocuments()
    {
        Validator().Validate([new("A.md", 1), new("a.md", 1)], null).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("doc.pdf")]
    [InlineData("noext")]
    [InlineData("a.md.exe")]
    public void UnsupportedExtensions_AreRejected_WithTheSupportedList(string name)
    {
        var result = Validate(name);

        result.Errors["files"].Single().Should().Contain("unsupported file type").And.Contain(".md").And.Contain(".png").And.Contain(".mp3");
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".MD")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".flac")]
    [InlineData(".opus")]
    public void SupportedExtensions_AreAccepted_CaseInsensitively(string ext) => Validate("file" + ext).IsValid.Should().BeTrue();

    [Fact]
    public void MaxChunkCharacters_DefaultsToTheServerLimit_AndMayOnlyBeLowered()
    {
        var settings = new IngestSettings { MaxChunkCharacters = 1000 };

        Validate("a.txt", 1, settings).MaxChunkCharacters.Should().Be(1000);
        Validate("a.txt", 1, settings, "250").MaxChunkCharacters.Should().Be(250);
        Validate("a.txt", 1, settings, "1000").IsValid.Should().BeTrue();
        Validate("a.txt", 1, settings, "1001").Errors.Should().ContainKey("maxChunkCharacters");
        Validate("a.txt", 1, settings, "0").Errors.Should().ContainKey("maxChunkCharacters");
        Validate("a.txt", 1, settings, "-3").Errors.Should().ContainKey("maxChunkCharacters");
        Validate("a.txt", 1, settings, "ten").Errors.Should().ContainKey("maxChunkCharacters");
        Validate("a.txt", 1, settings, "").IsValid.Should().BeTrue("blank means 'not supplied'");
    }

    [Fact]
    public void AllViolationsAreReportedTogether()
    {
        var result = Validator().Validate([new("../a.md", 1), new("b.pdf", 1)], "abc");

        result.Errors["files"].Should().HaveCount(2);
        result.Errors.Should().ContainKey("maxChunkCharacters");
    }
}
