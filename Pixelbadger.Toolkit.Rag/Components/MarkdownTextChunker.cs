namespace Pixelbadger.Toolkit.Rag.Components;

public class MarkdownTextChunker : ITextChunker
{
    public Task<List<IChunk>> ChunkTextAsync(string content)
    {
        var chunks = MarkdownChunker.ChunkByHeaders(content);
        var result = chunks.Select((chunk, index) => (IChunk)new MarkdownChunkWrapper(chunk, index + 1)).ToList();
        return Task.FromResult(result);
    }
}
