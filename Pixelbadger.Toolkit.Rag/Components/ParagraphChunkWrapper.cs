namespace Pixelbadger.Toolkit.Rag.Components;

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
