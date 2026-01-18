namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Factory for creating appropriate text chunkers based on file extension.
/// </summary>
public class ChunkerFactory
{
    private readonly ITextChunker _markdownChunker;
    private readonly ITextChunker _paragraphChunker;

    public ChunkerFactory(IEnumerable<ITextChunker> chunkers)
    {
        _markdownChunker = chunkers.OfType<MarkdownTextChunker>().First();
        _paragraphChunker = chunkers.OfType<ParagraphTextChunker>().First();
    }

    /// <summary>
    /// Gets the appropriate chunker for the given file path.
    /// Markdown files (.md) use MarkdownTextChunker.
    /// All other files use ParagraphTextChunker.
    /// </summary>
    /// <param name="filePath">The file path to get a chunker for.</param>
    /// <returns>The appropriate text chunker for the file type.</returns>
    public ITextChunker GetChunker(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        // Markdown files use header-based chunking
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return _markdownChunker;
        }

        // All other file types use paragraph chunking
        // (including .txt and future file types that will be converted to markdown)
        return _paragraphChunker;
    }
}
