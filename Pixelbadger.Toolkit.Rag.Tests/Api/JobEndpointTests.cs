using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Api;

public class JobEndpointTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static IngestJobListItemDto Job(IngestJobStatus status = IngestJobStatus.Queued) => new(
        JobId, DocId, "docs/guide.md", status, 1, 20000, 48321, null,
        new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 7, 0, 0, 1, DateTimeKind.Utc), null, null);

    private static void SetupPage(RagWebApplicationFactory factory, int page, int pageSize, IngestJobStatus? status, IngestJobPage result) =>
        factory.Queue.Setup(q => q.GetJobsAsync(page, pageSize, status, It.IsAny<CancellationToken>())).ReturnsAsync(result);

    [Fact]
    public async Task Jobs_DefaultsToPage1AndPageSize25_WithNoStatusFilter()
    {
        using var factory = new RagWebApplicationFactory();
        SetupPage(factory, 1, 25, null, new IngestJobPage([], 1, 25, 0));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Queue.VerifyAll();
    }

    [Fact]
    public async Task Jobs_ForwardsPageAndPageSize()
    {
        using var factory = new RagWebApplicationFactory();
        SetupPage(factory, 3, 10, null, new IngestJobPage([], 3, 10, 0));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/jobs?page=3&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Queue.VerifyAll();
    }

    [Theory]
    [InlineData("Queued", IngestJobStatus.Queued)]
    [InlineData("processing", IngestJobStatus.Processing)]
    [InlineData("SUCCEEDED", IngestJobStatus.Succeeded)]
    [InlineData("Skipped", IngestJobStatus.Skipped)]
    [InlineData("fAiLeD", IngestJobStatus.Failed)]
    public async Task Jobs_ParsesTheStatusFilterCaseInsensitively(string value, IngestJobStatus expected)
    {
        using var factory = new RagWebApplicationFactory();
        SetupPage(factory, 1, 25, expected, new IngestJobPage([], 1, 25, 0));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/jobs?status={value}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Queue.VerifyAll();
    }

    [Fact]
    public async Task Jobs_ReturnsJobsWithEnumsAsStrings_AndPagingMetadata()
    {
        using var factory = new RagWebApplicationFactory();
        SetupPage(factory, 2, 25, IngestJobStatus.Processing, new IngestJobPage([Job(IngestJobStatus.Processing)], 2, 25, 137));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/jobs?page=2&status=Processing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("page").GetInt32().Should().Be(2);
        root.GetProperty("pageSize").GetInt32().Should().Be(25);
        root.GetProperty("totalCount").GetInt32().Should().Be(137);
        root.GetProperty("totalPages").GetInt32().Should().Be(6);

        var job = root.GetProperty("jobs").EnumerateArray().Single();
        job.GetProperty("status").GetString().Should().Be("Processing");
        job.GetProperty("jobId").GetGuid().Should().Be(JobId);
        job.GetProperty("documentId").GetGuid().Should().Be(DocId);
        job.GetProperty("path").GetString().Should().Be("docs/guide.md");
        job.GetProperty("attempts").GetInt32().Should().Be(1);
        job.GetProperty("maxChunkCharacters").GetInt32().Should().Be(20000);
        job.GetProperty("sizeBytes").GetInt64().Should().Be(48321);
        job.GetProperty("chunkCount").ValueKind.Should().Be(JsonValueKind.Null);
        job.GetProperty("createdAtUtc").GetDateTime().Should().Be(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));
        job.GetProperty("completedAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        job.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        // Implementation-only fields never leave the server.
        job.TryGetProperty("content", out _).Should().BeFalse();
        job.TryGetProperty("leaseOwner", out _).Should().BeFalse();
        job.TryGetProperty("leaseExpiresAtUtc", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Jobs_EmptyResult_HasZeroTotalPages()
    {
        using var factory = new RagWebApplicationFactory();
        SetupPage(factory, 1, 25, null, new IngestJobPage([], 1, 25, 0));
        using var client = factory.CreateClient();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/jobs"));

        json.RootElement.GetProperty("jobs").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("totalPages").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("page=abc", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=-5", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=lots", "pageSize")]
    [InlineData("status=Bogus", "status")]
    [InlineData("status=2", "status")]
    [InlineData("status=99", "status")]
    public async Task Jobs_InvalidArguments_Return400ValidationProblem_NamingTheField(string query, string field)
    {
        using var factory = new RagWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/jobs?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        factory.Queue.Verify(
            q => q.GetJobsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IngestJobStatus?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Jobs_PageSize100_IsAccepted()
    {
        using var factory = new RagWebApplicationFactory();
        SetupPage(factory, 1, 100, null, new IngestJobPage([], 1, 100, 0));
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/jobs?pageSize=100")).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Queue.VerifyAll();
    }

    [Fact]
    public async Task Jobs_UnexpectedException_Returns500WithoutLeakingTheMessage()
    {
        using var factory = new RagWebApplicationFactory();
        factory.Queue
            .Setup(q => q.GetJobsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IngestJobStatus?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Server=secret.example;Password=hunter2"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("secret.example").And.NotContain("hunter2").And.NotContain("InvalidOperationException");
        body.Should().Contain("An unexpected error occurred.");
    }
}
