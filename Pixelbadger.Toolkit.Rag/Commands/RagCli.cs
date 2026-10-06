using System.CommandLine;

namespace Pixelbadger.Toolkit.Rag.Commands;

/// <summary>Builds the <c>pbrag</c> command tree.</summary>
public static class RagCli
{
    public static RootCommand Create(CliContext? context = null)
    {
        context ??= CliContext.Default;
        var root = new RootCommand("RAG toolkit: hybrid (Lucene.NET BM25 + SQL Server vector) search over text, image and audio, with an MCP server")
        {
            IngestCommand.Create(context),
            QueryCommand.Create(context),
            ServeCommand.Create(context)
        };
        return root;
    }

    /// <summary>Parses and runs <paramref name="args"/>; output and error go to the context's writers.</summary>
    public static Task<int> RunAsync(string[] args, CliContext? context = null)
    {
        context ??= CliContext.Default;
        var configuration = new InvocationConfiguration { Output = context.Out, Error = context.Err };
        return Create(context).Parse(args).InvokeAsync(configuration);
    }
}
