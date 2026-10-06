using System.CommandLine;

namespace Pixelbadger.Toolkit.Rag.Commands;

public static class ServeCommand
{
    public static Command Create(CliContext context)
    {
        var command = new Command("serve", "Host a stdio MCP server exposing a hybrid 'Search' tool over the index");
        var common = new CommonOptions();
        common.AddTo(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var ragOptions = common.Resolve(parseResult, context, requireExistingIndex: true);
                await context.RunMcpServer(ragOptions, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception ex)
            {
                // stdout belongs to the MCP protocol: errors go to stderr only.
                await context.Err.WriteLineAsync($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
