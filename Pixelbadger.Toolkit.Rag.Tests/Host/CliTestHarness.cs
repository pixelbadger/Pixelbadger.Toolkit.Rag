using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pixelbadger.Toolkit.Rag.Commands;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Host;

/// <summary>Runs the real command tree against mocked services, captured output and a fake environment.</summary>
public sealed class CliTestHarness : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "pbrag-host-" + Guid.NewGuid().ToString("N"));
    public string ModelDir { get; }
    public string IndexDir { get; }

    public Dictionary<string, string?> Env { get; } = new();
    public Mock<ISearchService> Search { get; } = new();
    public Mock<IContentIngester> Ingester { get; } = new();
    public Mock<IDocumentStore> Store { get; } = new();

    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();

    public RagOptions? BuiltWith { get; private set; }
    public RagOptions? ServedWith { get; private set; }
    public int BuildCount { get; private set; }

    public CliTestHarness()
    {
        ModelDir = Path.Combine(Directory, "model");
        IndexDir = Path.Combine(Directory, "index");
        System.IO.Directory.CreateDirectory(ModelDir);
        System.IO.Directory.CreateDirectory(IndexDir);
    }

    public CliContext Context => new()
    {
        GetEnvironmentVariable = name => Env.GetValueOrDefault(name),
        Out = Out,
        Err = Err,
        BuildServices = options =>
        {
            BuiltWith = options;
            BuildCount++;
            var services = new ServiceCollection();
            services.AddSingleton(Search.Object);
            services.AddSingleton(Ingester.Object);
            services.AddSingleton(Store.Object);
            return services.BuildServiceProvider();
        },
        RunMcpServer = (options, _) =>
        {
            ServedWith = options;
            return Task.CompletedTask;
        }
    };

    public Task<int> RunAsync(params string[] args) => RagCli.RunAsync(args, Context);

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, true);
    }
}
