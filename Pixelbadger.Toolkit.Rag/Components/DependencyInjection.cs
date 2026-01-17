using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Commands;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;

namespace Pixelbadger.Toolkit.Rag.Components;

public static class DependencyInjection
{
    public static IServiceCollection AddRagServices(this IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is required");
            var client = new OpenAI.OpenAIClient(apiKey);
            var embeddingClient = client.GetEmbeddingClient("text-embedding-3-large");
            return embeddingClient.AsIEmbeddingGenerator();
        });
        services.AddTransient<ITextChunker, SemanticTextChunker>();
        services.AddTransient<ILuceneRepository, LuceneRepository>();
        services.AddTransient<IVectorRepository, VectorRepository>();
        services.AddTransient<IReranker, RrfReranker>();

        // Register file readers
        services.AddTransient<IFileReader, PlainTextFileReader>();
        services.AddTransient<IFileReader, MarkdownFileReader>();
        services.AddTransient<FileReaderFactory>();

        services.AddTransient<SearchIndexer>((sp) =>
        {
            var luceneRepo = sp.GetRequiredService<ILuceneRepository>();
            var vectorRepo = sp.GetRequiredService<IVectorRepository>();
            var reranker = sp.GetRequiredService<IReranker>();
            var chunker = sp.GetRequiredService<ITextChunker>();
            var fileReaderFactory = sp.GetRequiredService<FileReaderFactory>();
            return new SearchIndexer(luceneRepo, vectorRepo, reranker, chunker, fileReaderFactory);
        });
        services.AddTransient<McpRagServer>();
        services.AddSingleton<IChatClient>(sp =>
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is required");
            var client = new OpenAI.OpenAIClient(apiKey);
            return client.GetChatClient("gpt-4o-mini").AsIChatClient();
        });

        // Register evaluation components
        services.AddTransient<EvalGenerator>();
        services.AddTransient<EvalValidator>();

        services.AddTransient<QueryCommand>();
        services.AddTransient<IngestCommand>();
        services.AddTransient<EvalCommand>();
        services.AddTransient<ServeCommand>();

        return services;
    }
}