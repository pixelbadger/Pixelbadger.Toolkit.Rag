using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Commands;

var rootCommand = new RootCommand("RAG toolkit for BM25 search indexing, querying, and MCP server functionality powered by Lucene.NET");

rootCommand.AddCommand(IngestCommand.Create());
rootCommand.AddCommand(QueryCommand.Create());
rootCommand.AddCommand(EvalCommand.Create());
rootCommand.AddCommand(ServeCommand.Create());

return await rootCommand.InvokeAsync(args);
