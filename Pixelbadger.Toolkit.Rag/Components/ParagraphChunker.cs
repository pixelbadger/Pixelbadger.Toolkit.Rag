namespace Pixelbadger.Toolkit.Rag.Components;

public class ParagraphChunk
{
    public string Content { get; set; } = string.Empty;
    public int ChunkNumber { get; set; }
}

public static class ParagraphChunker
{
    public static List<ParagraphChunk> ChunkByParagraphs(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<ParagraphChunk>();
        }

        // Split by double newlines (typical paragraph separator)
        var paragraphs = content
            .Split(new[] { "\r\n\r\n", "\n\n", "\r\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // If no double newlines found, split by single newlines
        if (paragraphs.Count == 1)
        {
            paragraphs = content
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }

        var chunks = new List<ParagraphChunk>();
        for (int i = 0; i < paragraphs.Count; i++)
        {
            chunks.Add(new ParagraphChunk
            {
                Content = paragraphs[i],
                ChunkNumber = i + 1
            });
        }

        return chunks;
    }
}
