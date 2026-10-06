using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Commands;

public static class QueryCommand
{
    public static Command Create(CliContext context)
    {
        var command = new Command("query", "Run a hybrid (BM25 + vector, RRF) search against the index");
        var common = new CommonOptions();
        common.AddTo(command);

        var query = new Option<string>("--query")
        {
            Description = "Search query text",
            Required = true
        };
        var maxResults = new Option<int>("--max-results")
        {
            Description = "Maximum number of results to return (1-100)",
            DefaultValueFactory = _ => 10
        };
        var sourceIds = new Option<string[]>("--source-ids", "--sourceIds")
        {
            Description = "Optional list of source IDs to constrain search results",
            AllowMultipleArgumentsPerToken = true
        };

        command.Options.Add(query);
        command.Options.Add(maxResults);
        command.Options.Add(sourceIds);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var ragOptions = common.Resolve(parseResult, context, requireExistingIndex: true);
                var provider = context.BuildServices(ragOptions);
                try
                {
                    var search = provider.GetRequiredService<ISearchService>();
                    var ids = parseResult.GetValue(sourceIds);
                    var results = await search.SearchAsync(
                        parseResult.GetValue(query)!,
                        parseResult.GetValue(maxResults),
                        ids is { Length: > 0 } ? ids : null,
                        cancellationToken);

                    await context.Out.WriteLineAsync(SearchResultFormatter.FormatForCli(results));
                    return 0;
                }
                finally
                {
                    await IngestCommand.DisposeAsync(provider);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await context.Err.WriteLineAsync("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                await context.Err.WriteLineAsync($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
