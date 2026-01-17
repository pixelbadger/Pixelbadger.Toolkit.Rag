namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>
/// Represents the validation result for a specific search mode.
/// </summary>
public record ModeResult
{
    public bool IsCorrect { get; init; }
    public string Explanation { get; init; } = "";
    public string RetrievedContent { get; init; } = "";
}
