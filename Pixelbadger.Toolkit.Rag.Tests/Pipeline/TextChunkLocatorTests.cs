using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests.Pipeline;

public class TextChunkLocatorTests
{
    private static async Task<(string Content, IReadOnlyList<string> Texts, IReadOnlyList<(long Start, long End)?> Locators)> RunAsync(ITextChunker chunker, string content)
    {
        var chunks = await chunker.ChunkTextAsync(content);
        var texts = chunks.Where(c => !string.IsNullOrWhiteSpace(c.Content)).Select(c => c.Content).ToList();
        return (content, texts, TextChunkLocator.Locate(content, texts));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Paragraphs_AreLocatedAtTheirOriginalOffsets(string nl)
    {
        var content = $"  First paragraph.{nl}{nl}Second paragraph  {nl}{nl}{nl}Third.{nl}";

        var (_, texts, locators) = await RunAsync(new ParagraphTextChunker(), content);

        texts.Should().HaveCount(3);
        for (int i = 0; i < texts.Count; i++)
        {
            var (start, end) = locators[i]!.Value;
            content.Substring((int)start, (int)(end - start)).Should().Be(texts[i]);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MarkdownSections_AreLocated_EvenWhenChunkerRejoinsLines(string nl)
    {
        var content = $"Intro text{nl}{nl}# One{nl}alpha{nl}beta{nl}{nl}## Two{nl}gamma{nl}";

        var (_, texts, locators) = await RunAsync(new MarkdownTextChunker(), content);

        texts.Should().HaveCount(3);
        locators.Should().OnlyContain(l => l.HasValue);
        locators.Select(l => l!.Value.Start).Should().BeInAscendingOrder();
        for (int i = 0; i < texts.Count; i++)
        {
            var (start, end) = locators[i]!.Value;
            var original = content.Substring((int)start, (int)(end - start));
            original.Replace("\r\n", "\n").Should().Be(texts[i].Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public void RepeatedChunkText_MapsToSuccessiveOccurrences()
    {
        var content = "same\n\nsame\n\nsame";

        var locators = TextChunkLocator.Locate(content, ["same", "same", "same"]);

        locators.Select(l => l!.Value.Start).Should().Equal(0, 6, 12);
    }

    [Fact]
    public void UnlocatableChunk_ReturnsNull()
    {
        TextChunkLocator.Locate("hello world", ["absent"]).Single().Should().BeNull();
    }
}
