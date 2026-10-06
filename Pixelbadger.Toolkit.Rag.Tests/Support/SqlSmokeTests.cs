using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Pixelbadger.Toolkit.Rag.Tests.Support;

[Collection("SqlServer")]
public class SqlSmokeTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Container_SupportsVectorType()
    {
        var cs = await sql.CreateDatabaseAsync();
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT VECTOR_DISTANCE('cosine', CAST('[1,0,0]' AS vector(3)), CAST('[1,0,0]' AS vector(3)))", conn);
        var distance = Convert.ToDouble(await cmd.ExecuteScalarAsync());
        distance.Should().BeApproximately(0, 1e-6);
    }
}
