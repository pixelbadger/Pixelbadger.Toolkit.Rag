using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Commands;

namespace Pixelbadger.Toolkit.Rag.Components;

public static class DependencyInjection
{
    public static IServiceCollection AddRagServices(this IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
        services.AddTransient<ITextChunker, SemanticTextChunker>();
        services.AddTransient<ILuceneRepository, LuceneRepository>();
        services.AddTransient<IVectorRepository, VectorRepository>();
        services.AddTransient<IReranker, RrfReranker>();
        services.AddTransient<SearchIndexer>((sp) =>
        {
            var luceneRepo = sp.GetRequiredService<ILuceneRepository>();
            var vectorRepo = sp.GetRequiredService<IVectorRepository>();
            var reranker = sp.GetRequiredService<IReranker>();
            var chunker = sp.GetRequiredService<ITextChunker>();
            return new SearchIndexer(luceneRepo, vectorRepo, reranker, chunker);
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

        services.AddTransient<QueryCommand>();
        services.AddTransient<IngestCommand>();
        services.AddTransient<EvalCommand>();
        services.AddTransient<ServeCommand>();

        return services;
    }
}