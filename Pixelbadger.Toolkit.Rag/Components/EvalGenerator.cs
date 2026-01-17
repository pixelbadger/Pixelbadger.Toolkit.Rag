using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Generates evaluation question-answer pairs from document content using an LLM.
/// </summary>
public class EvalGenerator
{
    private readonly IChatClient _chatClient;

    public EvalGenerator(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Generates evaluation questions and expected answers from the provided content.
    /// </summary>
    /// <param name="content">The document content to generate questions from.</param>
    /// <param name="count">The number of question-answer pairs to generate.</param>
    /// <returns>A list of evaluation question-answer pairs.</returns>
    public async Task<List<EvalPair>> GenerateAsync(string content, int count)
    {
        var prompt = $@"
Generate {count} diverse questions that can be answered using the information in the following document.
For each question, also provide the expected answer based on the document content.

Format the output as a JSON array of objects, each with 'question' and 'expectedAnswer' fields.

Document content:
{content}
";

        var response = await _chatClient.GetResponseAsync(prompt);
        var jsonText = response.Text ?? "[]";

        // Parse and validate JSON
        var evals = JsonSerializer.Deserialize<List<EvalPair>>(jsonText);
        if (evals == null || evals.Count == 0)
        {
            throw new InvalidOperationException("Failed to generate evaluation queries");
        }

        // Limit to requested count
        return evals.Take(count).ToList();
    }
}
