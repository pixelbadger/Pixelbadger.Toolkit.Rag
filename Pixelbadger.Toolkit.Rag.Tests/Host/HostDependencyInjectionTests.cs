using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Host;

public class HostDependencyInjectionTests
{
    // Deliberately points at nothing: constructors must be lazy (no model load, no DB connection).
    private static RagOptions NonexistentResources() => new()
    {
        IndexPath = Path.Combine(Path.GetTempPath(), "pbrag-does-not-exist"),
        Sql = new SqlStoreOptions { ConnectionString = "Server=nonexistent.invalid;Database=x;Connect Timeout=1" },
        Model = new EmbeddingModelOptions { ModelPath = Path.Combine(Path.GetTempPath(), "pbrag-no-model") }
    };

    private static ServiceProvider Build() =>
        new ServiceCollection().AddRagServices(NonexistentResources())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    [Fact]
    public void GraphValidatesOnBuild()
    {
        var act = () => Build();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(typeof(ISearchService))]
    [InlineData(typeof(IContentIngester))]
    [InlineData(typeof(IEmbeddingService))]
    [InlineData(typeof(IDocumentStore))]
    [InlineData(typeof(ILuceneRepository))]
    [InlineData(typeof(IReranker))]
    [InlineData(typeof(IImagePreprocessor))]
    [InlineData(typeof(IAudioPreprocessor))]
    [InlineData(typeof(IVisionEncoder))]
    [InlineData(typeof(IAudioEncoder))]
    [InlineData(typeof(OnnxSessionProvider))]
    [InlineData(typeof(ChunkerFactory))]
    [InlineData(typeof(FileReaderFactory))]
    [InlineData(typeof(RagOptions))]
    [InlineData(typeof(SqlStoreOptions))]
    [InlineData(typeof(EmbeddingModelOptions))]
    public void EveryContractResolvesWithoutModelOrDatabase(Type serviceType)
    {
        using var provider = Build();

        provider.GetRequiredService(serviceType).Should().NotBeNull();
    }

    [Fact]
    public void EveryRegisteredDescriptorConstructs()
    {
        var services = new ServiceCollection().AddRagServices(NonexistentResources());
        using var provider = services.BuildServiceProvider();

        // Our own registrations only (AddLogging adds open generics that can't be resolved by type alone).
        foreach (var descriptor in services.Where(d => d.ServiceType.Namespace?.StartsWith("Pixelbadger") == true))
            provider.GetServices(descriptor.ServiceType).Should().NotBeEmpty($"{descriptor.ServiceType} should resolve");
    }

    [Fact]
    public void OptionsAreSharedWithComponents()
    {
        var options = NonexistentResources();
        using var provider = new ServiceCollection().AddRagServices(options).BuildServiceProvider();

        provider.GetRequiredService<RagOptions>().Should().BeSameAs(options);
        provider.GetRequiredService<SqlStoreOptions>().Should().BeSameAs(options.Sql);
        provider.GetRequiredService<EmbeddingModelOptions>().Should().BeSameAs(options.Model);
    }

    [Fact]
    public void ExpensiveResourcesAreSingletons_StatelessServicesAreTransient()
    {
        using var provider = Build();

        provider.GetRequiredService<IEmbeddingService>().Should().BeSameAs(provider.GetRequiredService<IEmbeddingService>());
        provider.GetRequiredService<OnnxSessionProvider>().Should().BeSameAs(provider.GetRequiredService<OnnxSessionProvider>());
        provider.GetRequiredService<ISearchService>().Should().NotBeSameAs(provider.GetRequiredService<ISearchService>());
    }

    [Fact]
    public void ChunkersAndReadersAreAllRegistered()
    {
        using var provider = Build();

        provider.GetServices<ITextChunker>().Should().HaveCount(2);
        provider.GetServices<IFileReader>().Should().HaveCount(2);
    }

    [Fact]
    public void McpToolType_ResolvesFromTheSameGraph()
    {
        using var provider = Build();

        var tool = ActivatorUtilities.CreateInstance<McpRagServer>(provider);

        tool.Should().NotBeNull();
    }
}
