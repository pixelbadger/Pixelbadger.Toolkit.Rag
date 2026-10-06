using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Commands;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Host;

public class RagOptionsResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pbrag-resolver-" + Guid.NewGuid().ToString("N"));

    public RagOptionsResolverTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string? NoEnv(string _) => null;

    [Fact]
    public void BlankValues_AreTreatedAsMissing()
    {
        var act = () => RagOptionsResolver.Resolve("idx", "  ", _dir, false, NoEnv, false);

        act.Should().Throw<CliConfigurationException>().WithMessage("*connection string*");
    }

    [Fact]
    public void BlankCliValue_FallsBackToEnvironment()
    {
        string? Env(string n) => n switch
        {
            "PBRAG_CONNECTION_STRING" => "Server=env",
            "PBRAG_MODEL_PATH" => _dir,
            _ => null
        };

        var options = RagOptionsResolver.Resolve("idx", "", null, false, Env, false);

        options.Sql.ConnectionString.Should().Be("Server=env");
        options.Model.ModelPath.Should().Be(_dir);
        options.Model.Dimensions.Should().Be(256);
    }

    [Fact]
    public void ExactVectorSearch_MapsToExactOnly()
    {
        RagOptionsResolver.Resolve("idx", "c", _dir, true, NoEnv, false).Sql.SearchMode.Should().Be(VectorSearchMode.ExactOnly);
        RagOptionsResolver.Resolve("idx", "c", _dir, false, NoEnv, false).Sql.SearchMode.Should().Be(VectorSearchMode.Auto);
    }

    [Fact]
    public void MissingIndexPath_Throws()
    {
        var act = () => RagOptionsResolver.Resolve(null, "c", _dir, false, NoEnv, false);

        act.Should().Throw<CliConfigurationException>().WithMessage("*--index-path*");
    }

    [Fact]
    public void ExistingIndexIsOnlyRequiredWhenAsked()
    {
        var missing = Path.Combine(_dir, "nope");

        RagOptionsResolver.Resolve(missing, "c", _dir, false, NoEnv, requireExistingIndex: false).IndexPath.Should().Be(missing);
        var act = () => RagOptionsResolver.Resolve(missing, "c", _dir, false, NoEnv, requireExistingIndex: true);
        act.Should().Throw<CliConfigurationException>().WithMessage("*Index directory*not found*");
    }
}
