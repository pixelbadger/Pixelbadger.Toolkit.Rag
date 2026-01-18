namespace Pixelbadger.Toolkit.Rag.Components;

public class ParagraphTextChunker : ITextChunker
{
    public Task<List<IChunk>> ChunkTextAsync(string content)
    {
        var chunks = ParagraphChunker.ChunkByParagraphs(content);
        var result = chunks.Select(chunk => (IChunk)new ParagraphChunkWrapper(chunk)).ToList();
        return Task.FromResult(result);
    }
}
