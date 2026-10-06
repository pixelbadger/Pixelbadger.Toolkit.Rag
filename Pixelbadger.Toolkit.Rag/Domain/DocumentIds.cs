using System.Security.Cryptography;
using System.Text;

namespace Pixelbadger.Toolkit.Rag.Domain;

public static class DocumentIds
{
    /// <summary>
    /// The canonical form of a logical (caller-supplied) source path: separators normalised to '/' and
    /// leading '/' trimmed. Purely textual: no file system access, no <c>Path.GetFullPath</c>.
    /// </summary>
    public static string NormalizeLogicalPath(string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(logicalPath);
        return logicalPath.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// Deterministic document global id: "doc_" + first 32 hex chars of SHA-256 over the normalised
    /// logical path (case preserved), so the same logical path always maps to the same document
    /// regardless of where the bytes happen to be stored.
    /// </summary>
    public static string FromLogicalPath(string logicalPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeLogicalPath(logicalPath)));
        return "doc_" + Convert.ToHexStringLower(hash)[..32];
    }
}
