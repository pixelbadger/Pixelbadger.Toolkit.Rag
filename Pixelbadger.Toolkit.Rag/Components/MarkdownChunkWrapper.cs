namespace Pixelbadger.Toolkit.Rag.Components;

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
