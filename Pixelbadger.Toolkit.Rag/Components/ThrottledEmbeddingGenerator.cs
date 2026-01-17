using Microsoft.Extensions.AI;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Wraps an embedding generator to limit concurrent requests using a semaphore.
/// This prevents overwhelming rate-limited APIs like OpenAI.
/// </summary>
public class ThrottledEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly SemaphoreSlim _semaphore;

    public ThrottledEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        int maxConcurrency = 1)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await _inner.GenerateAsync(values, options, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        _semaphore.Dispose();
        _inner.Dispose();
    }
}
