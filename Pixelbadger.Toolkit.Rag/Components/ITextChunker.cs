namespace Pixelbadger.Toolkit.Rag.Components;

public interface ITextChunker
{
    Task<List<IChunk>> ChunkTextAsync(string content);
}
