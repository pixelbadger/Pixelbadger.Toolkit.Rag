namespace Pixelbadger.Toolkit.Rag.Components.FileReaders;

/// <summary>
/// File reader for plain text files (.txt).
/// </summary>
public class PlainTextFileReader : IFileReader
{
    /// <inheritdoc />
    public IEnumerable<string> SupportedExtensions => new[] { ".txt" };

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
