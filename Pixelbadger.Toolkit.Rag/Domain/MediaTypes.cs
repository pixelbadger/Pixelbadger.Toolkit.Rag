namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>
/// Extension-based modality routing for ingestion.
/// </summary>
public static class MediaTypes
{
    public static readonly IReadOnlySet<string> TextExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };

    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff" };

    public static readonly IReadOnlySet<string> AudioExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".aac" };

    public static Modality? GetModality(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (TextExtensions.Contains(ext)) return Modality.Text;
        if (ImageExtensions.Contains(ext)) return Modality.Image;
        if (AudioExtensions.Contains(ext)) return Modality.Audio;
        return null;
    }

    /// <summary>Media type (Content-Type) for a supported file, from its extension; null for unsupported extensions.</summary>
    public static string? GetContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".aac" => "audio/aac",
            _ => null
        };
}
