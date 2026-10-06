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
}
