using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Pixelbadger.Toolkit.Rag.Tests.Support;

/// <summary>
/// One SQL Server 2025 container per test run (shared via the "SqlServer" collection).
/// Each test/class calls <see cref="CreateDatabaseAsync"/> for an isolated, empty database
/// with PREVIEW_FEATURES = ON (needed for vector indexes on SQL Server 2025).
/// Requires Docker. Set PBRAG_TEST_SQL_CONNECTION_STRING (a server-level connection string) to
/// use an existing server instead of a container.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2025-latest";
    public const string ExternalServerEnvVar = "PBRAG_TEST_SQL_CONNECTION_STRING";

    private MsSqlContainer? _container;
    private string _serverConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerEnvVar);
        if (!string.IsNullOrWhiteSpace(external))
        {
            _serverConnectionString = external;
            return;
        }

        _container = new MsSqlBuilder().WithImage(Image).Build();
        await _container.StartAsync();
        _serverConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }

    /// <summary>Creates a fresh database and returns a connection string targeting it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "pbrag_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var conn = new SqlConnection(_serverConnectionString))
        {
            await conn.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]", conn);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = name, TrustServerCertificate = true };
        await using (var conn = new SqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var preview = new SqlCommand("ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON", conn);
            await preview.ExecuteNonQueryAsync();
        }

        return builder.ConnectionString;
    }
}

[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
