using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Api;

public class IngestEndpointTests
{
    private static readonly Guid JobId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static IngestJobStatusDto Status(IngestJobStatus status = IngestJobStatus.Queued, params IngestFileStatusDto[] files) => new(
        JobId, status, Attempts: 0, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc), null, null, null, files,
        new IngestJobSummaryDto(
            files.Count(f => f.Status == IngestFileStatus.Succeeded),
            files.Count(f => f.Status == IngestFileStatus.Failed),
            files.Count(f => f.Status == IngestFileStatus.Skipped)));

    private static RagWebApplicationFactory NewFactory(params (string Key, string Value)[] settings)
    {
        var factory = new RagWebApplicationFactory();
        foreach (var (key, value) in settings)
            factory.Settings[key] = value;
        return factory;
    }

    private static MultipartFormDataContent Upload(params (string FileName, string Content)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (fileName, content) in files)
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "files", fileName);
        return form;
    }

    private static void VerifyNothingEnqueued(RagWebApplicationFactory factory) =>
        factory.Queue.Verify(
            q => q.EnqueueAsync(It.IsAny<IReadOnlyList<IngestUpload>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

    // ---- POST /api/ingest: success ----

    [Fact]
    public async Task Post_ValidUpload_Returns202_WithLocationAndStatusBody()
    {
        using var factory = NewFactory();
        var captured = new List<(string Path, string Content)>();
        var maxChunk = 0;
        factory.Queue
            .Setup(q => q.EnqueueAsync(It.IsAny<IReadOnlyList<IngestUpload>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IngestUpload>, int, CancellationToken>((uploads, chunk, _) =>
            {
                maxChunk = chunk;
                foreach (var u in uploads)
                {
                    using var reader = new StreamReader(u.OpenRead());
                    captured.Add((u.LogicalPath, reader.ReadToEnd()));
                }
            })
            .ReturnsAsync(JobId);
        factory.Queue.Setup(q => q.GetJobAsync(JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Status(IngestJobStatus.Queued, new IngestFileStatusDto("docs/a.md", IngestFileStatus.Queued, null, null, null, null)));
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload(("docs/a.md", "# Hello"), ("b.txt", "plain")));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.Should().Be($"/api/ingest/{JobId}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("jobId").GetGuid().Should().Be(JobId);
        json.RootElement.GetProperty("status").GetString().Should().Be("Queued");
        json.RootElement.GetProperty("files")[0].GetProperty("status").GetString().Should().Be("Queued");
        captured.Should().Equal(("docs/a.md", "# Hello"), ("b.txt", "plain"));
        maxChunk.Should().Be(20000);
    }

    [Fact]
    public async Task Post_NormalisesBackslashPaths_AndAcceptsAllModalities()
    {
        using var factory = NewFactory();
        IReadOnlyList<IngestUpload>? uploads = null;
        factory.Queue
            .Setup(q => q.EnqueueAsync(It.IsAny<IReadOnlyList<IngestUpload>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IngestUpload>, int, CancellationToken>((u, _, _) => uploads = u.ToList())
            .ReturnsAsync(JobId);
        factory.Queue.Setup(q => q.GetJobAsync(JobId, It.IsAny<CancellationToken>())).ReturnsAsync(Status());
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload((@"win\dir\a.MD", "x"), ("pic.png", "x"), ("clip.mp3", "x")));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        uploads!.Select(u => u.LogicalPath).Should().Equal("win/dir/a.MD", "pic.png", "clip.mp3");
    }

    [Fact]
    public async Task Post_MaxChunkCharacters_CanLowerTheServerLimit()
    {
        using var factory = NewFactory();
        var maxChunk = 0;
        factory.Queue
            .Setup(q => q.EnqueueAsync(It.IsAny<IReadOnlyList<IngestUpload>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IngestUpload>, int, CancellationToken>((_, c, _) => maxChunk = c)
            .ReturnsAsync(JobId);
        factory.Queue.Setup(q => q.GetJobAsync(JobId, It.IsAny<CancellationToken>())).ReturnsAsync(Status());
        using var client = factory.CreateClient();
        var form = Upload(("a.txt", "x"));
        form.Add(new StringContent("500"), "maxChunkCharacters");

        var response = await client.PostAsync("/api/ingest", form);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        maxChunk.Should().Be(500);
    }

    // ---- POST /api/ingest: rejection (nothing is enqueued) ----

    private static async Task AssertRejectedAsync(RagWebApplicationFactory factory, HttpResponseMessage response, params string[] messageFragments)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        foreach (var fragment in messageFragments)
            body.Should().Contain(fragment);
        VerifyNothingEnqueued(factory);
    }

    [Fact]
    public async Task Post_NoFiles_Returns400()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        var form = new MultipartFormDataContent { { new StringContent("x"), "other" } };

        await AssertRejectedAsync(factory, await client.PostAsync("/api/ingest", form), "At least one file");
    }

    [Fact]
    public async Task Post_NotMultipart_Returns415()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ingest", new { files = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        VerifyNothingEnqueued(factory);
    }

    [Fact]
    public async Task Post_TooManyFiles_Returns400()
    {
        using var factory = NewFactory(("Rag:Ingest:MaxFilesPerJob", "2"));
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload(("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3")));

        await AssertRejectedAsync(factory, response, "exceeding the limit of 2");
    }

    [Fact]
    public async Task Post_FileTooLarge_Returns400()
    {
        using var factory = NewFactory(("Rag:Ingest:MaxFileSizeBytes", "16"));
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload(("ok.txt", "tiny"), ("big.txt", new string('x', 64))));

        await AssertRejectedAsync(factory, response, "limit 16 exceeded", "at most 16 bytes");
    }

    [Theory]
    [InlineData("doc.pdf")]
    [InlineData("noextension")]
    [InlineData("archive.zip")]
    public async Task Post_UnsupportedExtension_Returns400(string fileName)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload((fileName, "x")));

        await AssertRejectedAsync(factory, response, "unsupported file type", ".md");
    }

    [Theory]
    [InlineData("../secret.md")]
    [InlineData("docs/../../secret.md")]
    [InlineData("docs/./a.md")]
    [InlineData(@"..\secret.md")]
    public async Task Post_ParentOrDotSegments_Returns400(string fileName)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload((fileName, "x")));

        await AssertRejectedAsync(factory, response, "'.' or '..'");
    }

    [Theory]
    [InlineData("/etc/passwd.md")]
    [InlineData(@"\\server\share\a.md")]
    [InlineData(@"C:\docs\a.md")]
    [InlineData("c:/docs/a.md")]
    public async Task Post_RootedOrDrivePaths_Returns400(string fileName)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload((fileName, "x")));

        await AssertRejectedAsync(factory, response, "must be relative");
    }

    [Theory]
    [InlineData("docs//a.md")]
    [InlineData("docs/")]
    public async Task Post_EmptySegments_Returns400(string fileName)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload((fileName, "x")));

        await AssertRejectedAsync(factory, response, "empty segment");
    }

    [Fact]
    public async Task Post_DuplicateLogicalPaths_Returns400()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // The second path normalises to the first.
        var response = await client.PostAsync("/api/ingest", Upload(("docs/a.md", "1"), (@"docs\a.md", "2")));

        await AssertRejectedAsync(factory, response, "duplicate path");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("20001")]
    public async Task Post_InvalidMaxChunkCharacters_Returns400(string value)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        var form = Upload(("a.txt", "x"));
        form.Add(new StringContent(value), "maxChunkCharacters");

        await AssertRejectedAsync(factory, await client.PostAsync("/api/ingest", form), "maxChunkCharacters");
    }

    [Fact]
    public async Task Post_AnyViolation_RejectsTheWholeRequest()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ingest", Upload(("good.md", "ok"), ("../bad.md", "x")));

        await AssertRejectedAsync(factory, response, "../bad.md");
    }

    // ---- GET /api/ingest/{jobId} ----

    [Fact]
    public async Task Get_KnownJob_Returns200_WithStatusShape()
    {
        using var factory = NewFactory();
        var files = new[]
        {
            new IngestFileStatusDto("docs/a.md", IngestFileStatus.Succeeded, "doc_1", Modality.Text, 4, null),
            new IngestFileStatusDto("b.png", IngestFileStatus.Failed, null, null, null, "boom")
        };
        factory.Queue.Setup(q => q.GetJobAsync(JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Status(IngestJobStatus.Completed, files));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/ingest/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("status").GetString().Should().Be("Completed");
        root.GetProperty("attempts").GetInt32().Should().Be(0);
        root.GetProperty("createdAtUtc").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
        root.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        var first = root.GetProperty("files")[0];
        first.GetProperty("path").GetString().Should().Be("docs/a.md");
        first.GetProperty("status").GetString().Should().Be("Succeeded");
        first.GetProperty("documentId").GetString().Should().Be("doc_1");
        first.GetProperty("modality").GetString().Should().Be("Text");
        first.GetProperty("chunkCount").GetInt32().Should().Be(4);
        root.GetProperty("files")[1].GetProperty("error").GetString().Should().Be("boom");
        var summary = root.GetProperty("summary");
        summary.GetProperty("succeeded").GetInt32().Should().Be(1);
        summary.GetProperty("failed").GetInt32().Should().Be(1);
        summary.GetProperty("skipped").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Get_UnknownJob_Returns404ProblemDetails()
    {
        using var factory = NewFactory();
        factory.Queue.Setup(q => q.GetJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((IngestJobStatusDto?)null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/ingest/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Get_NonGuidId_Returns404()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ingest/not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Queue.VerifyNoOtherCalls();
    }
}
