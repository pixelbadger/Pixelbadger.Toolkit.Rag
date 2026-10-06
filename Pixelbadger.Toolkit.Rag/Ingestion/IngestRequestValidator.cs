using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>One uploaded file as seen by validation: the client-supplied name and the byte length.</summary>
public sealed record IngestFileCandidate(string FileName, long Length);

/// <param name="Paths">Normalised logical paths, in upload order (valid only when <see cref="IsValid"/>).</param>
/// <param name="MaxChunkCharacters">The effective chunk limit for the job.</param>
/// <param name="Errors">Field name to messages; empty when valid.</param>
public sealed record IngestValidationResult(
    IReadOnlyList<string> Paths,
    int MaxChunkCharacters,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Synchronous validation of an ingest upload. Any violation rejects the whole request, so nothing is enqueued.
/// </summary>
public sealed class IngestRequestValidator(IngestSettings settings, FileReaderFactory readers)
{
    public const string FilesField = "files";
    public const string MaxChunkCharactersField = "maxChunkCharacters";

    private const int MaxPathLength = 1024;

    public IngestValidationResult Validate(IReadOnlyList<IngestFileCandidate> files, string? maxChunkCharacters)
    {
        var fileErrors = new List<string>();
        var maxChunk = settings.MaxChunkCharacters;
        var errors = new Dictionary<string, string[]>();

        if (files.Count == 0)
            fileErrors.Add($"At least one file is required (multipart parts named '{FilesField}').");
        else if (files.Count > settings.MaxFilesPerJob)
            fileErrors.Add($"{files.Count} files were uploaded, exceeding the limit of {settings.MaxFilesPerJob} per job.");

        var paths = new List<string>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!TryNormalizePath(file.FileName, out var path, out var pathError))
            {
                fileErrors.Add($"'{file.FileName}': {pathError}");
                continue;
            }

            paths.Add(path);

            if (!seen.Add(path))
                fileErrors.Add($"'{path}': duplicate path in the same request.");

            if (file.Length > settings.MaxFileSizeBytes)
                fileErrors.Add($"'{path}': {file.Length} bytes exceeds the limit of {settings.MaxFileSizeBytes} bytes.");

            if (!IsSupported(path))
                fileErrors.Add($"'{path}': unsupported file type '{Path.GetExtension(path)}'. Supported: {string.Join(", ", SupportedExtensions)}.");
        }

        if (fileErrors.Count > 0)
            errors[FilesField] = fileErrors.ToArray();

        if (!string.IsNullOrWhiteSpace(maxChunkCharacters))
        {
            if (!int.TryParse(maxChunkCharacters, out var requested) || requested < 1)
                errors[MaxChunkCharactersField] = [$"'{MaxChunkCharactersField}' must be a positive integer."];
            else if (requested > settings.MaxChunkCharacters)
                errors[MaxChunkCharactersField] = [$"'{MaxChunkCharactersField}' cannot exceed the server limit of {settings.MaxChunkCharacters}."];
            else
                maxChunk = requested;
        }

        return new IngestValidationResult(paths, maxChunk, errors);
    }

    /// <summary>
    /// A safe logical path is relative and made of plain segments: no empty, "." or ".." segments, no drive
    /// letter, no control characters. Separators are normalised to '/'.
    /// </summary>
    public static bool TryNormalizePath(string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "the file name is empty.";
            return false;
        }

        if (raw.Any(char.IsControl))
        {
            error = "the path contains control characters.";
            return false;
        }

        var path = raw.Replace('\\', '/');
        if (path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            error = "the path must be relative (no leading '/' or drive letter).";
            return false;
        }

        if (path.Length > MaxPathLength)
        {
            error = $"the path exceeds {MaxPathLength} characters.";
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || string.IsNullOrWhiteSpace(segment))
            {
                error = "the path contains an empty segment.";
                return false;
            }

            if (segment is "." or "..")
            {
                error = "the path must not contain '.' or '..' segments.";
                return false;
            }
        }

        normalized = DocumentIds.NormalizeLogicalPath(path);
        error = string.Empty;
        return true;
    }

    private bool IsSupported(string path) => MediaTypes.GetModality(path) switch
    {
        Modality.Text => readers.CanRead(path),
        null => false,
        _ => true
    };

    private IEnumerable<string> SupportedExtensions =>
        readers.SupportedExtensions
            .Where(MediaTypes.TextExtensions.Contains)
            .Concat(MediaTypes.ImageExtensions)
            .Concat(MediaTypes.AudioExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
}
