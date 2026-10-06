namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>Text prompt formats of EmbeddingGemma 2 for search / RAG (reference §5). Media is never prefixed.</summary>
public static class GemmaPrompts
{
    public const string NoTitle = "none";

    /// <summary><c>task: search result | query: {query}</c></summary>
    public static string Query(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return $"task: search result | query: {query}";
    }

    /// <summary><c>title: {title|none} | text: {chunk}</c></summary>
    public static string Document(string? title, string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var t = string.IsNullOrWhiteSpace(title) ? NoTitle : title;
        return $"title: {t} | text: {chunk}";
    }
}
