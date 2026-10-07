using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Api;

public class DocumentContentEndpointTests
{
    private static readonly Guid Doc = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE];

    private static RagWebApplicationFactory NewFactory(DocumentContent? content)
    {
        var factory = new RagWebApplicationFactory();
        factory.Store.Setup(s => s.GetContentAsync(Doc, It.IsAny<CancellationToken>())).ReturnsAsync(content);
        return factory;
    }

    [Fact]
    public async Task Get_ReturnsTheExactBytes_WithMediaTypeAndInlineFileName()
    {
        using var factory = NewFactory(new DocumentContent(PngBytes, "image/png", "cat.png"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{Doc}/content");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PngBytes);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        response.Content.Headers.ContentDisposition.FileName.Should().Be("cat.png");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
    }

    [Fact]
    public async Task Get_Markdown_UsesTheStoredMediaType()
    {
        using var factory = NewFactory(new DocumentContent(Encoding.UTF8.GetBytes("# guide"), "text/markdown", "guide.md"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{Doc}/content");

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        (await response.Content.ReadAsStringAsync()).Should().Be("# guide");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("guide.md");
    }

    [Theory]
    [InlineData("?download=true")]
    [InlineData("?download=TRUE")]
    public async Task Get_WithDownloadTrue_IsAnAttachment_WithTheSameFileName(string query)
    {
        using var factory = NewFactory(new DocumentContent(PngBytes, "image/png", "cat.png"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{Doc}/content{query}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().Be("cat.png");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PngBytes);
    }

    [Fact]
    public async Task Get_WithDownloadFalse_IsInline()
    {
        using var factory = NewFactory(new DocumentContent(PngBytes, "image/png", "cat.png"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{Doc}/content?download=false");

        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
    }

    [Fact]
    public async Task Get_FileNameIsEncodedByTheFramework_NotComposedByHand()
    {
        using var factory = NewFactory(new DocumentContent([1, 2, 3], "audio/mpeg", "café \"live\".mp3"));
        using var client = factory.CreateClient();

        var inline = await client.GetAsync($"/api/documents/{Doc}/content");
        var attachment = await client.GetAsync($"/api/documents/{Doc}/content?download=true");

        foreach (var response in new[] { inline, attachment })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var header = string.Join(";", response.Content.Headers.GetValues("Content-Disposition"));
            header.Should().NotContain("é", "non-ASCII file names go through the RFC 5987 filename* form");
            response.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("café \"live\".mp3");
        }
    }

    [Fact]
    public async Task Get_Range_IsSupported_SoAudioCanSeek()
    {
        using var factory = NewFactory(new DocumentContent(PngBytes, "audio/wav", "a.wav"));
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{Doc}/content");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 4);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PngBytes[2..5]);
    }

    [Fact]
    public async Task Get_UnknownDocumentOrNoSourceYet_Is404ProblemDetails()
    {
        // The store answers null for both "no such document" and "never successfully indexed".
        using var factory = NewFactory(null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{Doc}/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        response.Content.Headers.Contains("Content-Disposition").Should().BeFalse();
    }

    [Fact]
    public async Task Get_NonGuidId_IsNotRouted()
    {
        using var factory = NewFactory(null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/documents/not-a-guid/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Store.Verify(s => s.GetContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Get_NeverTouchesTheQueue()
    {
        using var factory = NewFactory(new DocumentContent(PngBytes, "image/png", "cat.png"));
        using var client = factory.CreateClient();

        await client.GetAsync($"/api/documents/{Doc}/content");

        factory.Queue.VerifyNoOtherCalls();
    }
}
