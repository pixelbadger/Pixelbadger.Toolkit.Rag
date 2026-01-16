using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

public static class ServeCommand
{
    public static Command Create()
    {
        var command = new Command("serve", "Host an MCP server that performs BM25 queries against a Lucene.NET index");

        var indexPathOption = new Option<string>(
            aliases: ["--index-path"],
            description: "Path to the Lucene.NET index directory")
        {
            IsRequired = true
        };

        command.AddOption(indexPathOption);

        command.SetHandler(async (string indexPath) =>
        {
            try
            {
                if (!Directory.Exists(indexPath))
                {
                    Console.WriteLine($"Error: Index directory '{indexPath}' not found.");
                    Environment.Exit(1);
                }

                var server = new McpRagServer(indexPath);
                await server.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, indexPathOption);

        return command;
    }
}
