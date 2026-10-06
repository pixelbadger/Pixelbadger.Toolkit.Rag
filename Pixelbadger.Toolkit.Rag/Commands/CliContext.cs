using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

/// <summary>
/// Seams between the command handlers and the outside world (environment, DI, console, MCP host).
/// Production code uses <see cref="Default"/>; tests substitute their own.
/// </summary>
public sealed class CliContext
{
    /// <summary>Reads an environment variable (null when unset).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Builds the service provider for one invocation. Disposed by the command when it finishes.</summary>
    public Func<RagOptions, IServiceProvider> BuildServices { get; init; } = BuildDefaultServices;

    /// <summary>Runs the stdio MCP server until the host stops.</summary>
    public Func<RagOptions, CancellationToken, Task> RunMcpServer { get; init; } = RunDefaultMcpServer;

    public TextWriter Out { get; init; } = Console.Out;

    public TextWriter Err { get; init; } = Console.Error;

    public static CliContext Default => new();

    private static IServiceProvider BuildDefaultServices(RagOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging(ConfigureStderrLogging);
        services.AddRagServices(options);
        return services.BuildServiceProvider();
    }

    private static async Task RunDefaultMcpServer(RagOptions options, CancellationToken cancellationToken)
    {
        // Empty builder: no appsettings / env-var configuration surprises, and nothing writes to stdout
        // (stdout carries the MCP protocol).
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddRagServices(options);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<McpRagServer>();

        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
    }

    private static void ConfigureStderrLogging(ILoggingBuilder logging)
    {
        logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }
}
