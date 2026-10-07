using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Pixelbadger.Toolkit.Rag.Tests.Host;

/// <summary>
/// Azure SQL connection strings from the Aspire AppHost use <c>Authentication="Active Directory Default"</c>
/// (the app's managed identity). Since SqlClient 7 that provider lives in Microsoft.Data.SqlClient.Extensions.Azure,
/// which registers itself when referenced: without it, every connection to Azure SQL fails.
/// </summary>
public class SqlAuthenticationTests
{
    [Fact]
    public void ActiveDirectoryDefaultProviderIsRegistered()
    {
        SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault).Should().NotBeNull();
    }
}
