namespace Pixelbadger.Toolkit.Rag.Components.FileReaders;

/// <summary>
/// Factory for creating appropriate file readers based on file extension.
/// </summary>
public class FileReaderFactory
{
    private readonly IEnumerable<IFileReader> _readers;
    private readonly Dictionary<string, IFileReader> _readersByExtension;

    public FileReaderFactory(IEnumerable<IFileReader> readers)
    {
        _readers = readers;
        _readersByExtension = new Dictionary<string, IFileReader>(StringComparer.OrdinalIgnoreCase);

        foreach (var reader in _readers)
        {
            foreach (var extension in reader.SupportedExtensions)
            {
                _readersByExtension[extension] = reader;
            }
        }
    }

    /// <summary>
    /// Gets the appropriate file reader for the given file path.
    /// </summary>
    /// <param name="filePath">The file path to get a reader for.</param>
    /// <returns>The file reader that supports the file extension.</returns>
    /// <exception cref="NotSupportedException">Thrown when no reader supports the file extension.</exception>
    public IFileReader GetReader(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        if (string.IsNullOrEmpty(extension))
        {
            throw new NotSupportedException($"File has no extension: {filePath}");
        }

        if (_readersByExtension.TryGetValue(extension, out var reader))
        {
            return reader;
        }

        throw new NotSupportedException($"No file reader available for extension: {extension}");
    }

    /// <summary>
    /// Checks if a file reader is available for the given file path.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if a reader is available, false otherwise.</returns>
    public bool CanRead(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(extension) && _readersByExtension.ContainsKey(extension);
    }

    /// <summary>
    /// Gets all supported file extensions.
    /// </summary>
    public IEnumerable<string> SupportedExtensions => _readersByExtension.Keys;
}
