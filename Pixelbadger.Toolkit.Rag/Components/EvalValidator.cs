using Microsoft.Extensions.AI;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Validates retrieved content against expected answers using an LLM.
/// </summary>
public class EvalValidator
{
    private readonly IChatClient _chatClient;

    public EvalValidator(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Validates whether the retrieved content correctly answers the question.
    /// </summary>
    /// <param name="question">The evaluation question.</param>
    /// <param name="expectedAnswer">The expected answer.</param>
    /// <param name="retrievedContent">The content retrieved by the search system.</param>
    /// <returns>A tuple containing whether the answer is correct and an explanation.</returns>
    public async Task<(bool IsCorrect, string Explanation)> ValidateAsync(
        string question,
        string expectedAnswer,
        string retrievedContent)
    {
        var validationPrompt = $@"
Does the following response correctly answer the question?

Question: {question}
Expected Answer: {expectedAnswer}

Response: {retrievedContent}

Answer 'yes' or 'no' with a brief explanation.
";

        var validationResponse = await _chatClient.GetResponseAsync(validationPrompt);
        var validationText = validationResponse.Text ?? "";
        var isCorrect = validationText.ToLower().Contains("yes");

        return (isCorrect, validationText);
    }
}
