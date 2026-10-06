using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Support;

/// <summary>
/// The real web host with in-memory configuration. By default the hosted services (migrations, recovery, ingest worker)
/// are removed and <see cref="ISearchService"/> / <see cref="IIngestQueue"/> / <see cref="IDocumentStore"/> / <see cref="ILuceneRepository"/> are mocks, so tests need neither SQL nor a model.
/// </summary>
public sealed class RagWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbrag_web_" + Guid.NewGuid().ToString("N"));

    public Mock<ISearchService> Search { get; } = new();

    public Mock<IIngestQueue> Queue { get; } = new();

    /// <summary>Behind the real <c>DocumentService</c> (document delete).</summary>
    public Mock<IDocumentStore> Store { get; } = new();

    public Mock<ILuceneRepository> Lucene { get; } = new();

    /// <summary>Extra configuration (e.g. <c>Rag:Ingest:MaxFilesPerRequest</c>) applied on top of the defaults.</summary>
    public Dictionary<string, string?> Settings { get; } = new();

    /// <summary>When false (default) the search service, queue, store and Lucene repository are the mocks above.</summary>
    public bool UseRealServices { get; init; }

    /// <summary>When false (default) all hosted services are removed.</summary>
    public bool KeepHostedServices { get; init; }

    /// <summary>The service collection the host was built from (captured for registration checks).</summary>
    public IServiceCollection? ServiceDescriptors { get; private set; }

    public string ModelDirectory => Path.Combine(_root, "model");

    public string IndexDirectory => Path.Combine(_root, "index");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(ModelDirectory);

        var settings = new Dictionary<string, string?>
        {
            ["Rag:IndexPath"] = IndexDirectory,
            // Deliberately unreachable: nothing may connect during construction.
            ["Rag:ConnectionString"] = "Server=nonexistent.invalid;Database=x;Connect Timeout=1",
            ["Rag:ModelPath"] = ModelDirectory,
            ["Rag:ApplyMigrationsOnStartup"] = "false"
        };
        foreach (var (key, value) in Settings)
            settings[key] = value;

        // Same checks as the Development environment: every registration must be constructible and correctly scoped.
        builder.UseDefaultServiceProvider(o =>
        {
            o.ValidateOnBuild = true;
            o.ValidateScopes = true;
        });

        // UseSetting (not ConfigureAppConfiguration): Program binds configuration eagerly, before late-added sources exist.
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            ServiceDescriptors = services;
            if (!KeepHostedServices)
                services.RemoveAll<IHostedService>();

            if (!UseRealServices)
            {
                services.RemoveAll<ISearchService>();
                services.AddSingleton(Search.Object);
                services.RemoveAll<IIngestQueue>();
                services.AddSingleton(Queue.Object);
                services.RemoveAll<IDocumentStore>();
                services.AddSingleton(Store.Object);
                services.RemoveAll<ILuceneRepository>();
                services.AddSingleton(Lucene.Object);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
