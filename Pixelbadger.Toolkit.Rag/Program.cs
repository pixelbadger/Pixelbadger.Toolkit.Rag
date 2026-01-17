using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pixelbadger.Toolkit.Rag.Commands;
using Pixelbadger.Toolkit.Rag.Components;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services => services.AddRagServices())
    .Build();

var serviceProvider = host.Services;

var rootCommand = new RootCommand("RAG toolkit for BM25 search indexing, querying, and MCP server functionality powered by Lucene.NET");

var ingestCommand = serviceProvider.GetRequiredService<IngestCommand>();
var queryCommand = serviceProvider.GetRequiredService<QueryCommand>();
var evalCommand = serviceProvider.GetRequiredService<EvalCommand>();
var serveCommand = serviceProvider.GetRequiredService<ServeCommand>();

rootCommand.AddCommand(ingestCommand.Create());
rootCommand.AddCommand(queryCommand.Create());
rootCommand.AddCommand(evalCommand.Create());
rootCommand.AddCommand(serveCommand.Create());

return await rootCommand.InvokeAsync(args);
