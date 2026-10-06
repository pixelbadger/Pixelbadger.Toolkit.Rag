using FluentAssertions;
using Moq;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Host;

public class CommandTests : IDisposable
{
    private const string Conn = "Server=x;Database=y";
    private readonly CliTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string WriteContentFile()
    {
        var file = Path.Combine(_h.Directory, "content.txt");
        File.WriteAllText(file, "content");
        return file;
    }

    // ---------- ingest ----------

    [Fact]
    public async Task Ingest_BindsOptions_MigratesFirst_AndPassesLimits()
    {
        var file = WriteContentFile();
        var calls = new List<string>();
        _h.Store.Setup(s => s.MigrateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("migrate")).Returns(Task.CompletedTask);
        IngestOptions? captured = null;
        _h.Ingester.Setup(i => i.IngestFileAsync(file, It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IngestOptions?, CancellationToken>((_, o, _) => { calls.Add("ingest"); captured = o; })
            .ReturnsAsync(new IngestResult(file, "doc_abc", Modality.Text, 3));

        var exit = await _h.RunAsync("ingest",
            "--index-path", _h.IndexDir, "--content-path", file,
            "--connection-string", Conn, "--model-path", _h.ModelDir, "--exact-vector-search",
            "--max-file-size-bytes", "123", "--max-files", "7", "--max-chunk-characters", "456", "--allow-symlinks");

        exit.Should().Be(0, _h.Err.ToString());
        calls.Should().Equal("migrate", "ingest");
        captured.Should().NotBeNull();
        captured!.MaxFileSizeBytes.Should().Be(123);
        captured.MaxFiles.Should().Be(7);
        captured.MaxChunkCharacters.Should().Be(456);
        captured.AllowSymlinks.Should().BeTrue();

        _h.BuiltWith!.IndexPath.Should().Be(_h.IndexDir);
        _h.BuiltWith.Sql.ConnectionString.Should().Be(Conn);
        _h.BuiltWith.Sql.SearchMode.Should().Be(VectorSearchMode.ExactOnly);
        _h.BuiltWith.Model.ModelPath.Should().Be(_h.ModelDir);

        _h.Out.ToString().Should().Contain("OK").And.Contain("doc_abc").And.Contain("3 chunk(s)").And.Contain("Ingested 1 file(s), 0 failed, 0 skipped");
    }

    [Fact]
    public async Task Ingest_UsesDefaultLimits_AndAutoSearchMode()
    {
        var file = WriteContentFile();
        IngestOptions? captured = null;
        _h.Ingester.Setup(i => i.IngestFileAsync(file, It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IngestOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(new IngestResult(file, "doc_abc", Modality.Text, 1));

        var exit = await _h.RunAsync("ingest", "--index-path", _h.IndexDir, "--content-path", file,
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(0);
        captured!.MaxFileSizeBytes.Should().Be(IngestOptions.DefaultMaxFileSizeBytes);
        captured.MaxFiles.Should().Be(IngestOptions.DefaultMaxFiles);
        captured.MaxChunkCharacters.Should().Be(IngestOptions.DefaultMaxChunkCharacters);
        captured.AllowSymlinks.Should().BeFalse();
        _h.BuiltWith!.Sql.SearchMode.Should().Be(VectorSearchMode.Auto);
    }

    [Fact]
    public async Task Ingest_Folder_PrintsPerFileSummary_AndFailsWithExitCode1OnFailures()
    {
        var folder = Path.Combine(_h.Directory, "docs");
        Directory.CreateDirectory(folder);
        _h.Ingester.Setup(i => i.IngestFolderAsync(folder, It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestSummary(
                [new IngestResult("a.txt", "doc_a", Modality.Text, 2), new IngestResult("b.png", "doc_b", Modality.Image, 1)],
                [new IngestFailure("c.mp3", "ffmpeg not found")],
                4));

        var exit = await _h.RunAsync("ingest", "--index-path", _h.IndexDir, "--content-path", folder,
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(1);
        var output = _h.Out.ToString();
        output.Should().Contain("a.txt").And.Contain("Image").And.Contain("FAIL  c.mp3: ffmpeg not found");
        output.Should().Contain("Ingested 2 file(s), 1 failed, 4 skipped");
    }

    [Fact]
    public async Task Ingest_MissingContentPath_ReturnsNonZeroWithMessageOnStderr()
    {
        var exit = await _h.RunAsync("ingest", "--index-path", _h.IndexDir, "--content-path", Path.Combine(_h.Directory, "nope"),
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("Path not found");
        _h.BuildCount.Should().Be(0);
    }

    [Fact]
    public async Task Ingest_IngesterException_IsReportedAsError()
    {
        var file = WriteContentFile();
        _h.Ingester.Setup(i => i.IngestFileAsync(file, It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("boom"));

        var exit = await _h.RunAsync("ingest", "--index-path", _h.IndexDir, "--content-path", file,
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("Error: boom");
    }

    [Fact]
    public async Task Ingest_NoVectorsOption_IsRejected()
    {
        var file = WriteContentFile();
        var exit = await _h.RunAsync("ingest", "--index-path", _h.IndexDir, "--content-path", file,
            "--connection-string", Conn, "--model-path", _h.ModelDir, "--no-vectors");

        exit.Should().NotBe(0);
        _h.BuildCount.Should().Be(0);
    }

    [Fact]
    public async Task Ingest_RequiresIndexPathAndContentPath()
    {
        var exit = await _h.RunAsync("ingest", "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().NotBe(0);
        _h.Err.ToString().Should().Contain("--index-path").And.Contain("--content-path");
    }

    // ---------- env fallbacks / missing config ----------

    [Fact]
    public async Task EnvironmentVariables_ProvideConnectionStringAndModelPath()
    {
        var file = WriteContentFile();
        _h.Env["PBRAG_CONNECTION_STRING"] = "Server=env";
        _h.Env["PBRAG_MODEL_PATH"] = _h.ModelDir;
        _h.Ingester.Setup(i => i.IngestFileAsync(file, It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestResult(file, "doc_abc", Modality.Text, 1));

        var exit = await _h.RunAsync("ingest", "--index-path", _h.IndexDir, "--content-path", file);

        exit.Should().Be(0, _h.Err.ToString());
        _h.BuiltWith!.Sql.ConnectionString.Should().Be("Server=env");
        _h.BuiltWith.Model.ModelPath.Should().Be(_h.ModelDir);
    }

    [Fact]
    public async Task CliOptions_TakePrecedenceOverEnvironment()
    {
        var file = WriteContentFile();
        var otherModel = Path.Combine(_h.Directory, "model2");
        Directory.CreateDirectory(otherModel);
        _h.Env["PBRAG_CONNECTION_STRING"] = "Server=env";
        _h.Env["PBRAG_MODEL_PATH"] = _h.ModelDir;
        _h.Ingester.Setup(i => i.IngestFileAsync(file, It.IsAny<IngestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestResult(file, "doc_abc", Modality.Text, 1));

        var exit = await _h.RunAsync("ingest", "--index-path", _h.IndexDir, "--content-path", file,
            "--connection-string", "Server=cli", "--model-path", otherModel);

        exit.Should().Be(0);
        _h.BuiltWith!.Sql.ConnectionString.Should().Be("Server=cli");
        _h.BuiltWith.Model.ModelPath.Should().Be(otherModel);
    }

    [Theory]
    [InlineData("ingest")]
    [InlineData("query")]
    [InlineData("serve")]
    public async Task MissingConnectionString_IsAClearError(string command)
    {
        var args = BaseArgs(command).Concat(["--model-path", _h.ModelDir]).ToArray();

        var exit = await _h.RunAsync(args);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("connection string").And.Contain("PBRAG_CONNECTION_STRING");
        _h.BuildCount.Should().Be(0);
        _h.ServedWith.Should().BeNull();
    }

    [Theory]
    [InlineData("ingest")]
    [InlineData("query")]
    [InlineData("serve")]
    public async Task MissingModelPath_IsAClearError(string command)
    {
        var args = BaseArgs(command).Concat(["--connection-string", Conn]).ToArray();

        var exit = await _h.RunAsync(args);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("model path").And.Contain("PBRAG_MODEL_PATH");
        _h.BuildCount.Should().Be(0);
    }

    [Fact]
    public async Task NonexistentModelDirectory_IsAClearError()
    {
        var args = BaseArgs("query").Concat(["--connection-string", Conn, "--model-path", Path.Combine(_h.Directory, "missing")]).ToArray();

        var exit = await _h.RunAsync(args);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("Model directory").And.Contain("not found");
    }

    [Theory]
    [InlineData("query")]
    [InlineData("serve")]
    public async Task MissingIndexDirectory_IsAClearError_ForQueryAndServe(string command)
    {
        var missingIndex = Path.Combine(_h.Directory, "no-index");
        var args = BaseArgs(command, missingIndex).Concat(["--connection-string", Conn, "--model-path", _h.ModelDir]).ToArray();

        var exit = await _h.RunAsync(args);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("Index directory").And.Contain("not found");
    }

    private string[] BaseArgs(string command, string? indexDir = null)
    {
        var index = indexDir ?? _h.IndexDir;
        return command switch
        {
            "ingest" => ["ingest", "--index-path", index, "--content-path", WriteContentFile()],
            "query" => ["query", "--index-path", index, "--query", "hello"],
            _ => ["serve", "--index-path", index]
        };
    }

    // ---------- query ----------

    [Fact]
    public async Task Query_BindsOptions_AndPrintsResults()
    {
        var chunkId = Guid.NewGuid();
        _h.Search.Setup(s => s.SearchAsync("red planet", 3, It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SearchResult
                {
                    Score = 0.0325f, ChunkId = chunkId, DocumentId = "doc_1", SourceFile = "mars.txt", SourceId = "src",
                    Ordinal = 2, Modality = Modality.Text, LocatorStart = 10, LocatorEnd = 99, Content = "Mars is red."
                }
            });

        var exit = await _h.RunAsync("query", "--index-path", _h.IndexDir, "--query", "red planet", "--max-results", "3",
            "--source-ids", "a", "b", "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(0, _h.Err.ToString());
        _h.Search.Verify(s => s.SearchAsync("red planet", 3,
            It.Is<IReadOnlyCollection<string>?>(ids => ids != null && ids.SequenceEqual(new[] { "a", "b" })),
            It.IsAny<CancellationToken>()), Times.Once);
        var output = _h.Out.ToString();
        output.Should().Contain("Result 1 (Score: 0.0325)").And.Contain(chunkId.ToString()).And.Contain("doc_1")
            .And.Contain("mars.txt").And.Contain("Text").And.Contain("chars 10–99").And.Contain("Mars is red.");
    }

    [Fact]
    public async Task Query_DefaultsMaxResultsTo10_AndNoSourceFilter()
    {
        _h.Search.Setup(s => s.SearchAsync("q", 10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SearchResult>());

        var exit = await _h.RunAsync("query", "--index-path", _h.IndexDir, "--query", "q",
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(0, _h.Err.ToString());
        _h.Out.ToString().Should().Contain("No results found.");
        _h.Search.VerifyAll();
    }

    [Fact]
    public async Task Query_LegacySourceIdsAliasStillBinds()
    {
        _h.Search.Setup(s => s.SearchAsync("q", 10, It.Is<IReadOnlyCollection<string>?>(ids => ids!.Count == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SearchResult>());

        var exit = await _h.RunAsync("query", "--index-path", _h.IndexDir, "--query", "q", "--sourceIds", "only",
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(0, _h.Err.ToString());
        _h.Search.VerifyAll();
    }

    [Fact]
    public async Task Query_SearchModeOptionIsRemoved()
    {
        var exit = await _h.RunAsync("query", "--index-path", _h.IndexDir, "--query", "q", "--search-mode", "bm25",
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().NotBe(0);
        _h.BuildCount.Should().Be(0);
    }

    [Fact]
    public async Task Query_ValidationErrorFromService_ReturnsNonZeroAndMessage()
    {
        _h.Search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("maxResults must be between 1 and 100"));

        var exit = await _h.RunAsync("query", "--index-path", _h.IndexDir, "--query", "q", "--max-results", "1000",
            "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("maxResults must be between 1 and 100");
    }

    // ---------- serve ----------

    [Fact]
    public async Task Serve_ResolvesOptionsAndRunsServer_WithoutWritingToStdout()
    {
        var exit = await _h.RunAsync("serve", "--index-path", _h.IndexDir, "--connection-string", Conn, "--model-path", _h.ModelDir);

        exit.Should().Be(0, _h.Err.ToString());
        _h.ServedWith.Should().NotBeNull();
        _h.ServedWith!.IndexPath.Should().Be(_h.IndexDir);
        _h.ServedWith.Sql.ConnectionString.Should().Be(Conn);
        _h.Out.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Serve_ServerFailure_GoesToStderrWithExitCode1()
    {
        var context = new Pixelbadger.Toolkit.Rag.Commands.CliContext
        {
            GetEnvironmentVariable = _ => null,
            Out = _h.Out,
            Err = _h.Err,
            RunMcpServer = (_, _) => throw new InvalidOperationException("host failed")
        };

        var exit = await Pixelbadger.Toolkit.Rag.Commands.RagCli.RunAsync(
            ["serve", "--index-path", _h.IndexDir, "--connection-string", Conn, "--model-path", _h.ModelDir], context);

        exit.Should().Be(1);
        _h.Err.ToString().Should().Contain("host failed");
        _h.Out.ToString().Should().BeEmpty();
    }

    // ---------- tree shape ----------

    [Fact]
    public void RootCommand_ExposesIngestQueryServe()
    {
        var root = Pixelbadger.Toolkit.Rag.Commands.RagCli.Create(_h.Context);

        root.Subcommands.Select(c => c.Name).Should().BeEquivalentTo("ingest", "query", "serve");
    }
}
