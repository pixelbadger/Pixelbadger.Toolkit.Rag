namespace Pixelbadger.Toolkit.Rag.Dtos;

/// <summary>
/// Represents the result of evaluating a question across different search modes.
/// </summary>
public record EvalResult
{
    public string Question { get; init; } = "";
    public string ExpectedAnswer { get; init; } = "";
    public Dictionary<string, ModeResult> ModeResults { get; } = new();
}
