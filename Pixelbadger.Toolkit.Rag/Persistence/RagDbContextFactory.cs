using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pixelbadger.Toolkit.Rag.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>. Uses PBRAG_CONNECTION_STRING when set; the fallback only
/// exists so migrations can be scaffolded without a live database.
/// </summary>
public sealed class RagDbContextFactory : IDesignTimeDbContextFactory<RagDbContext>
{
    public RagDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlStoreOptions.ConnectionStringEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = "Server=localhost,1433;Database=pbrag;User Id=sa;Password=Design_Time_Only1!;TrustServerCertificate=True";

        return new RagDbContext(new DbContextOptionsBuilder<RagDbContext>().UseSqlServer(connectionString).Options);
    }
}
