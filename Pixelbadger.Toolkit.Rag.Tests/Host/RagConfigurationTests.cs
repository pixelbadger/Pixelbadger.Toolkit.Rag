using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Pixelbadger.Toolkit.Rag.Configuration;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Host;

public class RagConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbrag_cfg_" + Guid.NewGuid().ToString("N"));

    public RagConfigurationTests() => Directory.CreateDirectory(ModelDir);

    private string ModelDir => Path.Combine(_root, "model");

    private string IndexDir => Path.Combine(_root, "index");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // Later entries win, so ConfigWith can override the valid baseline.
    private IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToLookup(v => v.Key).Select(g => new KeyValuePair<string, string?>(g.Key, g.Last().Value))).Build();

    private (string, string?)[] Valid() =>
    [
        ("Rag:IndexPath", IndexDir), ("Rag:ConnectionString", "Server=x"), ("Rag:ModelPath", ModelDir)
    ];

    private IConfiguration ConfigWith(params (string Key, string? Value)[] extra) => Config([.. Valid(), .. extra]);

    [Fact]
    public void Bind_BuildsOptions_WithDocumentedDefaults()
    {
        var options = RagConfiguration.Bind(Config(Valid()));

        options.IndexPath.Should().Be(IndexDir);
        options.Sql.ConnectionString.Should().Be("Server=x");
        options.Sql.SearchMode.Should().Be(VectorSearchMode.Auto);
        options.Model.ModelPath.Should().Be(ModelDir);
        options.ApplyMigrationsOnStartup.Should().BeTrue();
        options.Ingest.MaxFileSizeBytes.Should().Be(10 * 1024 * 1024);
        options.Ingest.MaxFilesPerRequest.Should().Be(100);
        options.Ingest.MaxChunkCharacters.Should().Be(20000);
        options.Ingest.MaxAttempts.Should().Be(3);
        options.Ingest.LeaseSeconds.Should().Be(600);
        options.Ingest.PollIntervalSeconds.Should().Be(2);
        options.Ingest.CancelTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Bind_CreatesTheIndexDirectory_WhenMissing()
    {
        Directory.Exists(IndexDir).Should().BeFalse();

        RagConfiguration.Bind(Config(Valid()));

        Directory.Exists(IndexDir).Should().BeTrue();
    }

    [Fact]
    public void Bind_ReadsExplicitValues()
    {
        var options = RagConfiguration.Bind(ConfigWith(
            ("Rag:ExactVectorSearch", "true"),
            ("Rag:ApplyMigrationsOnStartup", "false"),
            ("Rag:Ingest:MaxFileSizeBytes", "2048"),
            ("Rag:Ingest:MaxFilesPerRequest", "5"),
            ("Rag:Ingest:MaxChunkCharacters", "900"),
            ("Rag:Ingest:MaxAttempts", "7"),
            ("Rag:Ingest:LeaseSeconds", "30"),
            ("Rag:Ingest:PollIntervalSeconds", "9"),
            ("Rag:Ingest:CancelTimeoutSeconds", "4")));

        options.Sql.SearchMode.Should().Be(VectorSearchMode.ExactOnly);
        options.ApplyMigrationsOnStartup.Should().BeFalse();
        options.Ingest.MaxFileSizeBytes.Should().Be(2048);
        options.Ingest.MaxFilesPerRequest.Should().Be(5);
        options.Ingest.MaxChunkCharacters.Should().Be(900);
        options.Ingest.MaxAttempts.Should().Be(7);
        options.Ingest.Lease.Should().Be(TimeSpan.FromSeconds(30));
        options.Ingest.PollInterval.Should().Be(TimeSpan.FromSeconds(9));
        options.Ingest.CancelTimeout.Should().Be(TimeSpan.FromSeconds(4));
        options.Ingest.MaxRequestBodyBytes.Should().Be(5 * 2048 + 1024 * 1024);
    }

    [Fact]
    public void Bind_FallsBackToPbragEnvironmentVariables()
    {
        var options = RagConfiguration.Bind(Config(
            ("PBRAG_INDEX_PATH", IndexDir), ("PBRAG_CONNECTION_STRING", "Server=env"), ("PBRAG_MODEL_PATH", ModelDir)));

        options.IndexPath.Should().Be(IndexDir);
        options.Sql.ConnectionString.Should().Be("Server=env");
        options.Model.ModelPath.Should().Be(ModelDir);
    }

    [Fact]
    public void Bind_PrefersTheRagSectionOverTheFallbackVariables()
    {
        var options = RagConfiguration.Bind(ConfigWith(("PBRAG_CONNECTION_STRING", "Server=env")));

        options.Sql.ConnectionString.Should().Be("Server=x");
    }

    [Fact]
    public void Bind_TreatsBlankValuesAsMissing_SoTheFallbackApplies()
    {
        var options = RagConfiguration.Bind(Config(
            ("Rag:IndexPath", ""), ("Rag:ConnectionString", " "), ("Rag:ModelPath", ""),
            ("PBRAG_INDEX_PATH", IndexDir), ("PBRAG_CONNECTION_STRING", "Server=env"), ("PBRAG_MODEL_PATH", ModelDir)));

        options.Sql.ConnectionString.Should().Be("Server=env");
    }

    [Theory]
    [InlineData("Rag:IndexPath", "Missing index path")]
    [InlineData("Rag:ConnectionString", "Missing SQL Server connection string")]
    [InlineData("Rag:ModelPath", "Missing embedding model path")]
    public void Bind_Throws_WithAHelpfulMessage_WhenARequiredValueIsMissing(string missingKey, string message)
    {
        var config = Config(Valid().Where(v => v.Item1 != missingKey).ToArray());

        var act = () => RagConfiguration.Bind(config);

        act.Should().Throw<RagConfigurationException>().WithMessage($"{message}*");
    }

    [Fact]
    public void Bind_Throws_WhenTheModelDirectoryDoesNotExist()
    {
        var config = ConfigWith(("Rag:ModelPath", Path.Combine(_root, "nope")));

        var act = () => RagConfiguration.Bind(config);

        act.Should().Throw<RagConfigurationException>().WithMessage("Model directory '*nope' not found.");
    }

    [Theory]
    [InlineData("MaxFileSizeBytes", "0")]
    [InlineData("MaxFilesPerRequest", "-1")]
    [InlineData("MaxChunkCharacters", "0")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("LeaseSeconds", "0")]
    [InlineData("PollIntervalSeconds", "0")]
    [InlineData("CancelTimeoutSeconds", "0")]
    public void Bind_Throws_WhenAnIngestLimitIsNotPositive(string key, string value)
    {
        var act = () => RagConfiguration.Bind(ConfigWith(($"Rag:Ingest:{key}", value)));

        act.Should().Throw<RagConfigurationException>().WithMessage($"Rag:Ingest:{key} must be greater than zero.");
    }

    [Fact]
    public void Bind_Throws_RagConfigurationException_ForUnparsableValues()
    {
        var act = () => RagConfiguration.Bind(ConfigWith(("Rag:Ingest:MaxAttempts", "many")));

        act.Should().Throw<RagConfigurationException>().WithMessage("Invalid configuration value*");
    }

    [Fact]
    public void Bind_DoesNotTouchTheModelOrTheDatabase()
    {
        // A connection string pointing nowhere and an empty model directory are fine: only presence is validated.
        var act = () => RagConfiguration.Bind(ConfigWith(("Rag:ConnectionString", "Server=nonexistent.invalid;Connect Timeout=1")));

        act.Should().NotThrow();
    }
}
