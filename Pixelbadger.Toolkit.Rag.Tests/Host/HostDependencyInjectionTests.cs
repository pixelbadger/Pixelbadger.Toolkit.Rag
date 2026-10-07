using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Components.FileReaders;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Mcp;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Host;

/// <summary>
/// Builds the real web host (Program) against a nonexistent SQL Server and an empty model directory: constructors
/// must be lazy (no model load, no DB connection). The factory enables scope and build validation.
/// </summary>
public class HostDependencyInjectionTests
{
    private static RagWebApplicationFactory NewFactory() => new() { UseRealServices = true };

    [Fact]
    public void GraphValidatesOnBuild()
    {
        using var factory = NewFactory();

        var act = () => factory.Services;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(typeof(ISearchService))]
    [InlineData(typeof(IContentIngester))]
    [InlineData(typeof(IEmbeddingService))]
    [InlineData(typeof(IDocumentStore))]
    [InlineData(typeof(IImagePreprocessor))]
    [InlineData(typeof(IAudioPreprocessor))]
    [InlineData(typeof(IVisionEncoder))]
    [InlineData(typeof(IAudioEncoder))]
    [InlineData(typeof(OnnxSessionProvider))]
    [InlineData(typeof(ChunkerFactory))]
    [InlineData(typeof(FileReaderFactory))]
    [InlineData(typeof(IIngestQueue))]
    [InlineData(typeof(IngestRequestValidator))]
    [InlineData(typeof(IngestWorkerSignal))]
    [InlineData(typeof(IngestJobRegistry))]
    [InlineData(typeof(InFlightJobRecovery))]
    [InlineData(typeof(DocumentService))]
    [InlineData(typeof(RagOptions))]
    [InlineData(typeof(SqlStoreOptions))]
    [InlineData(typeof(EmbeddingModelOptions))]
    [InlineData(typeof(IngestSettings))]
    public void EveryContractResolvesWithoutModelOrDatabase(Type serviceType)
    {
        using var factory = NewFactory();

        factory.Services.GetRequiredService(serviceType).Should().NotBeNull();
    }

    [Fact]
    public void EveryRegisteredDescriptorConstructs()
    {
        using var factory = NewFactory();
        _ = factory.Services; // build the host

        // Our own registrations only (the framework's are exercised by the host itself).
        var ours = factory.ServiceDescriptors!
            .Where(d => d.ServiceType.Namespace?.StartsWith("Pixelbadger") == true)
            .ToList();
        ours.Should().NotBeEmpty();
        foreach (var descriptor in ours)
            factory.Services.GetServices(descriptor.ServiceType).Should().NotBeEmpty($"{descriptor.ServiceType} should resolve");
    }

    [Theory]
    [InlineData(typeof(DatabaseMigrationHostedService))]
    [InlineData(typeof(InFlightJobRecoveryHostedService))]
    [InlineData(typeof(IngestWorker))]
    public void HostedServicesConstructWithoutTouchingTheDatabase(Type hostedType)
    {
        using var factory = NewFactory();

        var instance = ActivatorUtilities.CreateInstance(factory.Services, hostedType);

        instance.Should().BeAssignableTo<IHostedService>();
    }

    [Fact]
    public void HostedServices_AreRegistered_MigrationsThenInFlightReset_ThenWorker()
    {
        // Hosted services start in registration order: migrations, then the in-flight job reset, then the worker's first poll.
        using var factory = new RagWebApplicationFactory { UseRealServices = true, KeepHostedServices = true };
        _ = factory.Services;

        var hosted = factory.ServiceDescriptors!
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        hosted.Should().ContainInOrder(typeof(DatabaseMigrationHostedService), typeof(InFlightJobRecoveryHostedService), typeof(IngestWorker));
    }

    [Fact]
    public void OptionsAreSharedWithComponents()
    {
        using var factory = NewFactory();
        var options = factory.Services.GetRequiredService<RagOptions>();

        factory.Services.GetRequiredService<SqlStoreOptions>().Should().BeSameAs(options.Sql);
        factory.Services.GetRequiredService<EmbeddingModelOptions>().Should().BeSameAs(options.Model);
        factory.Services.GetRequiredService<IngestSettings>().Should().BeSameAs(options.Ingest);
        options.Model.ModelPath.Should().Be(factory.ModelDirectory);
    }

    [Fact]
    public void ExpensiveResourcesAreSingletons_StatelessServicesAreTransient()
    {
        using var factory = NewFactory();
        var sp = factory.Services;

        sp.GetRequiredService<IEmbeddingService>().Should().BeSameAs(sp.GetRequiredService<IEmbeddingService>());
        sp.GetRequiredService<OnnxSessionProvider>().Should().BeSameAs(sp.GetRequiredService<OnnxSessionProvider>());
        sp.GetRequiredService<IngestWorkerSignal>().Should().BeSameAs(sp.GetRequiredService<IngestWorkerSignal>());
        sp.GetRequiredService<IngestJobRegistry>().Should().BeSameAs(sp.GetRequiredService<IngestJobRegistry>());
        sp.GetRequiredService<InFlightJobRecovery>().Should().BeSameAs(sp.GetRequiredService<InFlightJobRecovery>());
        sp.GetRequiredService<DocumentService>().Should().NotBeSameAs(sp.GetRequiredService<DocumentService>());
        sp.GetRequiredService<ISearchService>().Should().NotBeSameAs(sp.GetRequiredService<ISearchService>());
        sp.GetRequiredService<IIngestQueue>().Should().NotBeSameAs(sp.GetRequiredService<IIngestQueue>());
    }

    [Fact]
    public void ChunkersAndReadersAreAllRegistered()
    {
        using var factory = NewFactory();

        factory.Services.GetServices<ITextChunker>().Should().HaveCount(2);
        factory.Services.GetServices<IFileReader>().Should().HaveCount(2);
    }

    [Fact]
    public void McpToolType_ResolvesFromTheSameGraph()
    {
        using var factory = NewFactory();

        var tool = ActivatorUtilities.CreateInstance<McpRagServer>(factory.Services);

        tool.Should().NotBeNull();
    }
}
