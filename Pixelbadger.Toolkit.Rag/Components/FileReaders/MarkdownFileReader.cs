namespace Pixelbadger.Toolkit.Rag.Components.FileReaders;

/// <summary>
/// File reader for Markdown files (.md).
/// </summary>
public class MarkdownFileReader : IFileReader
{
    /// <inheritdoc />
    public IEnumerable<string> SupportedExtensions => new[] { ".md" };

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        return await File.ReadAllTextAsync(filePath);
    }
}
