using System.Security.Cryptography;
using System.Text;

namespace Pixelbadger.Toolkit.Rag.Domain;

public static class DocumentIds
{
    /// <summary>
    /// Deterministic document global id: "doc_" + first 32 hex chars of SHA-256 over the
    /// full, normalised path (case preserved; separators normalised to '/').
    /// </summary>
    public static string FromSourcePath(string sourcePath)
    {
        var normalised = Path.GetFullPath(sourcePath).Replace('\\', '/');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return "doc_" + Convert.ToHexStringLower(hash)[..32];
    }
}
