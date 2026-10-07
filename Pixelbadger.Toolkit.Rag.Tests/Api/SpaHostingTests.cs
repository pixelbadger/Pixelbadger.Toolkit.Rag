using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Api;

public sealed class SpaHostingTests : IDisposable
{
    private const string Html = "<!doctype html><html><body><div id=\"root\">spa-marker</div></body></html>";
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "pbrag_wwwroot_" + Guid.NewGuid().ToString("N"));

    private RagWebApplicationFactory WithUi()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), Html);
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js"), "console.log(1);");
        var factory = new RagWebApplicationFactory();
        factory.Settings[WebHostDefaults.WebRootKey] = _webRoot;
        return factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/query")]
    [InlineData("/ingest")]
    [InlineData("/documents/0198a1b2/nested")]
    public async Task Navigation_returns_index_html(string path)
    {
        using var factory = WithUi();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("spa-marker");
    }

    [Fact]
    public async Task Static_assets_are_served()
    {
        using var factory = WithUi();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/assets/app.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("console.log");
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api")]
    [InlineData("/mcp/nope")]
    [InlineData("/assets/missing.js")]
    public async Task Reserved_paths_and_missing_files_stay_404_not_html(string path)
    {
        using var factory = WithUi();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("spa-marker");
    }

    [Fact]
    public async Task Health_is_not_swallowed()
    {
        using var factory = WithUi();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("spa-marker");
    }

    [Fact]
    public async Task Mcp_endpoint_is_not_swallowed()
    {
        using var factory = WithUi();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        (await response.Content.ReadAsStringAsync()).Should().NotContain("spa-marker");
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    [Fact]
    public async Task Api_method_mismatch_is_not_turned_into_html_and_non_get_is_untouched()
    {
        using var factory = WithUi();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/query");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("spa-marker");

        var post = await client.PostAsync("/query", null);
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Missing_web_root_answers_404_without_failing_startup()
    {
        using var factory = new RagWebApplicationFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/query")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public void Dispose()
    {
        try { Directory.Delete(_webRoot, true); } catch { /* best effort */ }
    }
}
