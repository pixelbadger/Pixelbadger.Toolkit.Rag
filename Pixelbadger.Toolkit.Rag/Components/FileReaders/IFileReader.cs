namespace Pixelbadger.Toolkit.Rag.Components.FileReaders;

/// <summary>
/// Interface for reading different file types and extracting their text content.
/// </summary>
public interface IFileReader
{
    /// <summary>
    /// Reads the text content from the specified file asynchronously.
    /// </summary>
    /// <param name="filePath">The path to the file to read.</param>
    /// <returns>A task that represents the asynchronous read operation. The task result contains the text content of the file.</returns>
    Task<string> ReadTextAsync(string filePath);

    /// <summary>
    /// Gets the file extensions this reader supports (e.g., ".txt", ".md").
    /// </summary>
    IEnumerable<string> SupportedExtensions { get; }
}
