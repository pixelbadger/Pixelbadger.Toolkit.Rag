namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>
/// The logical (caller-supplied) path of an uploaded file. It is metadata only (<see cref="Document.SourcePath"/>,
/// <see cref="Document.Title"/>): documents are identified by their server-assigned id, never by path.
/// </summary>
public static class LogicalPath
{
    /// <summary>
    /// The canonical form: separators normalised to '/' and leading '/' trimmed. Purely textual: no file system
    /// access, no <c>Path.GetFullPath</c>.
    /// </summary>
    public static string Normalize(string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(logicalPath);
        return logicalPath.Replace('\\', '/').TrimStart('/');
    }
}
