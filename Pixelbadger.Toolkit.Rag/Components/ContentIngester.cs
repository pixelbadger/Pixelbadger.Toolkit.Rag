using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Components;

// STUB (Phase 0). Implemented by workstream E1. The previous implementation's path-safety
// logic (symlink refusal, root containment, size/count limits) must be carried over; see git history.
public class ContentIngester : IContentIngester
{
    public ContentIngester(
        RagOptions options,
        IDocumentStore store,
        ILuceneRepository lucene,
        IEmbeddingService embeddings,
        ChunkerFactory chunkerFactory,
        FileReaderFactory fileReaderFactory,
        IImagePreprocessor imagePreprocessor,
        IAudioPreprocessor audioPreprocessor) { }

    public Task<IngestResult> IngestFileAsync(string filePath, IngestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IngestSummary> IngestFolderAsync(string folderPath, IngestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
