namespace Pixelbadger.Toolkit.Rag.Components;

public interface IChunk
{
    string Content { get; }
    int ChunkNumber { get; }
}

public interface ITextChunker
{
    Task<List<IChunk>> ChunkTextAsync(string content);
}

public class MarkdownChunkWrapper : IChunk
{
    private readonly MarkdownChunk _chunk;
    public MarkdownChunkWrapper(MarkdownChunk chunk, int chunkNumber)
    {
        _chunk = chunk;
        ChunkNumber = chunkNumber;
    }

    public string Content => _chunk.Content;
    public int ChunkNumber { get; }
    public MarkdownChunk OriginalChunk => _chunk;
}

public class ParagraphChunkWrapper : IChunk
{
    private readonly ParagraphChunk _chunk;
    public ParagraphChunkWrapper(ParagraphChunk chunk)
    {
        _chunk = chunk;
    }

    public string Content => _chunk.Content;
    public int ChunkNumber => _chunk.ChunkNumber;
    public ParagraphChunk OriginalChunk => _chunk;
}

public class MarkdownTextChunker : ITextChunker
{
    public Task<List<IChunk>> ChunkTextAsync(string content)
    {
        var chunks = MarkdownChunker.ChunkByHeaders(content);
        var result = chunks.Select((chunk, index) => (IChunk)new MarkdownChunkWrapper(chunk, index + 1)).ToList();
        return Task.FromResult(result);
    }
}

public class ParagraphTextChunker : ITextChunker
{
    public Task<List<IChunk>> ChunkTextAsync(string content)
    {
        var chunks = ParagraphChunker.ChunkByParagraphs(content);
        var result = chunks.Select(chunk => (IChunk)new ParagraphChunkWrapper(chunk)).ToList();
        return Task.FromResult(result);
    }
}
