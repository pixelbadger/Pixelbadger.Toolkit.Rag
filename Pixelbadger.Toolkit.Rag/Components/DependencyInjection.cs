using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Text;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Components;

public static class DependencyInjection
{
    /// <summary>Registers the RAG pipeline for one invocation's <paramref name="options"/>.</summary>
    public static IServiceCollection AddRagServices(this IServiceCollection services, RagOptions options)
    {
        services.AddLogging();

        services.AddSingleton(options);
        services.AddSingleton(options.Sql);
        services.AddSingleton(options.Model);
        services.AddSingleton(options.Ingest);

        // Embeddings (expensive resources → singletons)
        services.AddSingleton<OnnxSessionProvider>();
        services.AddSingleton<IVisionEncoder, VisionEncoder>();
        services.AddSingleton<IAudioEncoder, AudioEncoder>();
        services.AddSingleton<IEmbeddingService, GemmaEmbeddingService>();
        services.AddTransient<IImagePreprocessor, ImagePreprocessor>();
        services.AddTransient<IAudioPreprocessor, AudioPreprocessor>();

        // Persistence
        services.AddTransient<IDocumentStore, SqlDocumentStore>();
        services.AddTransient<ILuceneRepository, LuceneRepository>();

        // Ingest queue (SQL) + request validation
        services.AddTransient<IIngestQueue, SqlIngestQueue>();
        services.AddTransient<IngestRequestValidator>();
        services.AddSingleton<IngestWorkerSignal>();

        // Chunking / reading
        services.AddTransient<ITextChunker, MarkdownTextChunker>();
        services.AddTransient<ITextChunker, ParagraphTextChunker>();
        services.AddTransient<ChunkerFactory>();
        services.AddTransient<IFileReader, PlainTextFileReader>();
        services.AddTransient<IFileReader, MarkdownFileReader>();
        services.AddTransient<FileReaderFactory>();

        // Pipeline
        services.AddTransient<IReranker, RrfReranker>();
        services.AddTransient<IContentIngester, ContentIngester>();
        services.AddTransient<ISearchService, SearchService>();

        return services;
    }

    /// <summary>
    /// Registers the background pieces of the web host. Order matters: hosted services start in registration
    /// order, so migrations run before the worker starts polling.
    /// </summary>
    public static IServiceCollection AddRagHostedServices(this IServiceCollection services)
    {
        services.AddHostedService<DatabaseMigrationHostedService>();
        services.AddHostedService<IngestWorker>();
        return services;
    }
}
