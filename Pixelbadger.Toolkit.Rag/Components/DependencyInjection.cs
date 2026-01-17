using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Pixelbadger.Toolkit.Rag.Commands;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Polly;
using System.Net;

namespace Pixelbadger.Toolkit.Rag.Components;

public static class DependencyInjection
{
    public static IServiceCollection AddRagServices(this IServiceCollection services)
    {
        // Configure HttpClient with retry policy for OpenAI API
        services.AddHttpClient("OpenAI")
            .AddStandardResilienceHandler(options =>
            {
                // Configure retry policy for rate limiting (429) and transient errors
                options.Retry.MaxRetryAttempts = 5;
                options.Retry.Delay = TimeSpan.FromSeconds(2);
                options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;

                // Prefer Retry-After header value, fallback to exponential backoff
                options.Retry.DelayGenerator = args =>
                {
                    var response = args.Outcome.Result;
                    if (response?.Headers.RetryAfter != null)
                    {
                        // Use Retry-After header if present
                        if (response.Headers.RetryAfter.Delta.HasValue)
                        {
                            return new ValueTask<TimeSpan?>(response.Headers.RetryAfter.Delta.Value);
                        }
                        else if (response.Headers.RetryAfter.Date.HasValue)
                        {
                            var delay = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                            return new ValueTask<TimeSpan?>(delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
                        }
                    }

                    // Fallback to default exponential backoff with jitter
                    return ValueTask.FromResult<TimeSpan?>(null);
                };

                // Handle 429 (Too Many Requests) specifically
                options.Retry.ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response =>
                        response.StatusCode == HttpStatusCode.TooManyRequests || // 429
                        response.StatusCode == HttpStatusCode.RequestTimeout || // 408
                        response.StatusCode == HttpStatusCode.ServiceUnavailable || // 503
                        (int)response.StatusCode >= 500); // 5xx errors

                // Configure circuit breaker to prevent overwhelming the API
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.FailureRatio = 0.5;
                options.CircuitBreaker.MinimumThroughput = 10;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

                // Configure timeout
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
            });

        services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is required");

            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("OpenAI");
            var client = new OpenAI.OpenAIClient(apiKey, new OpenAI.OpenAIClientOptions { Transport = new OpenAI.HttpClientTransport(httpClient) });
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

            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("OpenAI");
            var client = new OpenAI.OpenAIClient(apiKey, new OpenAI.OpenAIClientOptions { Transport = new OpenAI.HttpClientTransport(httpClient) });
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