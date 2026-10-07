using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Api;

public class DocumentEndpointTests
{
    private static readonly Guid DocA = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid DocB = Guid.Parse("1a8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid JobA = Guid.Parse("2b8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid JobB = Guid.Parse("3c8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTime Created = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static DocumentDto Document(Guid id, Guid jobId, string path = "docs/a.md", IngestJobStatus status = IngestJobStatus.Queued,
        IndexStatus index = IndexStatus.Queued, int chunks = 0, string? error = null) => new(
        id, path, Path.GetFileName(path), Modality.Text, index, chunks, Created,
        new IngestJobDto(jobId, status, Attempts: 0, Created, null, null, error));

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

    private static void VerifyNothingEnqueued(RagWebApplicationFactory factory)
    {
        factory.Queue.Verify(
            q => q.EnqueueNewDocumentsAsync(It.IsAny<IReadOnlyList<IngestUpload>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        factory.Queue.Verify(
            q => q.EnqueueReingestAsync(It.IsAny<Guid>(), It.IsAny<IngestUpload>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static void SetupCreate(RagWebApplicationFactory factory, List<(string Path, string Content)>? captured = null, Action<int>? maxChunk = null)
    {
        var ids = new Queue<(Guid Doc, Guid Job)>([(DocA, JobA), (DocB, JobB)]);
        factory.Queue
            .Setup(q => q.EnqueueNewDocumentsAsync(It.IsAny<IReadOnlyList<IngestUpload>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<IngestUpload> uploads, int chunk, CancellationToken _) =>
            {
                maxChunk?.Invoke(chunk);
                var created = new List<EnqueuedDocument>();
                foreach (var u in uploads)
                {
                    using var reader = new StreamReader(u.OpenRead());
                    captured?.Add((u.LogicalPath, reader.ReadToEnd()));
                    var (doc, job) = ids.Dequeue();
                    created.Add(new EnqueuedDocument(doc, job));
                }

                return created;
            });
        factory.Queue.Setup(q => q.GetDocumentAsync(DocA, It.IsAny<CancellationToken>())).ReturnsAsync(Document(DocA, JobA, "docs/a.md"));
        factory.Queue.Setup(q => q.GetDocumentAsync(DocB, It.IsAny<CancellationToken>())).ReturnsAsync(Document(DocB, JobB, "b.txt"));
    }

    // ---- POST /api/documents: success ----

    [Fact]
    public async Task Post_Batch_Returns202_WithDocumentsInUploadOrder_AndNoLocation()
    {
        using var factory = NewFactory();
        var captured = new List<(string Path, string Content)>();
        var maxChunk = 0;
        SetupCreate(factory, captured, c => maxChunk = c);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/documents", Upload(("docs/a.md", "# Hello"), ("b.txt", "plain")));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().BeNull("a Location only makes sense for a single document");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var documents = json.RootElement.GetProperty("documents");
        documents.GetArrayLength().Should().Be(2);
        documents[0].GetProperty("documentId").GetGuid().Should().Be(DocA);
        documents[0].GetProperty("path").GetString().Should().Be("docs/a.md");
        documents[0].GetProperty("title").GetString().Should().Be("a.md");
        documents[0].GetProperty("modality").GetString().Should().Be("Text");
        documents[0].GetProperty("indexStatus").GetString().Should().Be("Queued");
        documents[0].GetProperty("chunkCount").GetInt32().Should().Be(0);
        documents[0].GetProperty("latestJob").GetProperty("jobId").GetGuid().Should().Be(JobA);
        documents[0].GetProperty("latestJob").GetProperty("status").GetString().Should().Be("Queued");
        documents[1].GetProperty("documentId").GetGuid().Should().Be(DocB);
        captured.Should().Equal(("docs/a.md", "# Hello"), ("b.txt", "plain"));
        maxChunk.Should().Be(20000);
    }

    [Fact]
    public async Task Post_SingleFile_Returns202_WithLocationOfTheDocument()
    {
        using var factory = NewFactory();
        SetupCreate(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/documents", Upload(("docs/a.md", "# Hello")));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.Should().Be($"/api/documents/{DocA}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("documents").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Post_NormalisesBackslashPaths_AcceptsAllModalities_AndAllowsRepeatedPaths()
    {
        using var factory = NewFactory();
        IReadOnlyList<IngestUpload>? uploads = null;
        factory.Queue
            .Setup(q => q.EnqueueNewDocumentsAsync(It.IsAny<IReadOnlyList<IngestUpload>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IngestUpload>, int, CancellationToken>((u, _, _) => uploads = u.ToList())
            .ReturnsAsync([new EnqueuedDocument(DocA, JobA), new EnqueuedDocument(DocB, JobB), new EnqueuedDocument(Guid.NewGuid(), Guid.NewGuid()), new EnqueuedDocument(Guid.NewGuid(), Guid.NewGuid())]);
        factory.Queue.Setup(q => q.GetDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Document(DocA, JobA));
        using var client = factory.CreateClient();

        // The last two are the same path: two different documents.
        var response = await client.PostAsync("/api/documents", Upload((@"win\dir\a.MD", "x"), ("pic.png", "x"), ("clip.mp3", "x"), ("clip.mp3", "y")));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        uploads!.Select(u => u.LogicalPath).Should().Equal("win/dir/a.MD", "pic.png", "clip.mp3", "clip.mp3");
    }

    [Fact]
    public async Task Post_MaxChunkCharacters_CanLowerTheServerLimit()
    {
        using var factory = NewFactory();
        var maxChunk = 0;
        SetupCreate(factory, maxChunk: c => maxChunk = c);
        using var client = factory.CreateClient();
        var form = Upload(("a.txt", "x"));
        form.Add(new StringContent("500"), "maxChunkCharacters");

        var response = await client.PostAsync("/api/documents", form);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        maxChunk.Should().Be(500);
    }

    // ---- POST /api/documents: rejection (nothing is created) ----

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

        await AssertRejectedAsync(factory, await client.PostAsync("/api/documents", form), "At least one file");
    }

    [Fact]
    public async Task Post_NotMultipart_Returns415()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/documents", new { files = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        VerifyNothingEnqueued(factory);
    }

    [Fact]
    public async Task Post_TooManyFiles_Returns400()
    {
        using var factory = NewFactory(("Rag:Ingest:MaxFilesPerRequest", "2"));
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/documents", Upload(("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3")));

        await AssertRejectedAsync(factory, response, "exceeding the limit of 2");
    }

    [Fact]
    public async Task Post_FileTooLarge_Returns400()
    {
        using var factory = NewFactory(("Rag:Ingest:MaxFileSizeBytes", "16"));
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/documents", Upload(("ok.txt", "tiny"), ("big.txt", new string('x', 64))));

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

        var response = await client.PostAsync("/api/documents", Upload((fileName, "x")));

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

        var response = await client.PostAsync("/api/documents", Upload((fileName, "x")));

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

        var response = await client.PostAsync("/api/documents", Upload((fileName, "x")));

        await AssertRejectedAsync(factory, response, "must be relative");
    }

    [Theory]
    [InlineData("docs//a.md")]
    [InlineData("docs/")]
    public async Task Post_EmptySegments_Returns400(string fileName)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/documents", Upload((fileName, "x")));

        await AssertRejectedAsync(factory, response, "empty segment");
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

        await AssertRejectedAsync(factory, await client.PostAsync("/api/documents", form), "maxChunkCharacters");
    }

    [Fact]
    public async Task Post_AnyViolation_RejectsTheWholeRequest()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/documents", Upload(("good.md", "ok"), ("../bad.md", "x")));

        await AssertRejectedAsync(factory, response, "../bad.md");
    }

    // ---- GET /api/documents/{id} ----

    [Fact]
    public async Task Get_KnownDocument_Returns200_WithTheDocumentShape()
    {
        using var factory = NewFactory();
        var document = Document(DocA, JobA, "docs/a.md", IngestJobStatus.Succeeded, IndexStatus.Indexed, chunks: 4);
        factory.Queue.Setup(q => q.GetDocumentAsync(DocA, It.IsAny<CancellationToken>())).ReturnsAsync(document);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{DocA}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("documentId").GetGuid().Should().Be(DocA);
        root.GetProperty("indexStatus").GetString().Should().Be("Indexed");
        root.GetProperty("chunkCount").GetInt32().Should().Be(4);
        root.GetProperty("updatedAtUtc").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
        var job = root.GetProperty("latestJob");
        job.GetProperty("status").GetString().Should().Be("Succeeded");
        job.GetProperty("attempts").GetInt32().Should().Be(0);
        job.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_UnknownDocument_Returns404ProblemDetails()
    {
        using var factory = NewFactory();
        factory.Queue.Setup(q => q.GetDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DocumentDto?)null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Get_NonGuidId_Returns404()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/documents/not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Queue.VerifyNoOtherCalls();
    }

    // ---- POST /api/documents/{id} (re-ingest) ----

    [Theory]
    [InlineData(ReingestOutcome.Created)]
    [InlineData(ReingestOutcome.ReplacedQueued)]
    public async Task Reingest_QueuedOrReplaced_Returns202_WithDocumentAndLocation(ReingestOutcome outcome)
    {
        using var factory = NewFactory();
        string? path = null;
        string? content = null;
        factory.Queue
            .Setup(q => q.EnqueueReingestAsync(DocA, It.IsAny<IngestUpload>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IngestUpload, int, CancellationToken>((_, u, _, _) =>
            {
                path = u.LogicalPath;
                using var reader = new StreamReader(u.OpenRead());
                content = reader.ReadToEnd();
            })
            .ReturnsAsync(new ReingestResult(outcome, JobA));
        factory.Queue.Setup(q => q.GetDocumentAsync(DocA, It.IsAny<CancellationToken>())).ReturnsAsync(Document(DocA, JobA, "docs/v2.md"));
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/documents/{DocA}", Upload(("docs/v2.md", "second version")));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.Should().Be($"/api/documents/{DocA}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("documentId").GetGuid().Should().Be(DocA);
        json.RootElement.GetProperty("path").GetString().Should().Be("docs/v2.md");
        (path, content).Should().Be(("docs/v2.md", "second version"));
    }

    [Fact]
    public async Task Reingest_WhileProcessing_Returns409WithAnExplanation()
    {
        using var factory = NewFactory();
        factory.Queue
            .Setup(q => q.EnqueueReingestAsync(DocA, It.IsAny<IngestUpload>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReingestResult(ReingestOutcome.Conflict));
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/documents/{DocA}", Upload(("a.md", "x")));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("being processed");
    }

    [Fact]
    public async Task Reingest_UnknownDocument_Returns404()
    {
        using var factory = NewFactory();
        factory.Queue
            .Setup(q => q.EnqueueReingestAsync(It.IsAny<Guid>(), It.IsAny<IngestUpload>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReingestResult(ReingestOutcome.NotFound));
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/documents/{DocA}", Upload(("a.md", "x")));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Reingest_NotExactlyOneFile_Returns400(int fileCount)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        var files = Enumerable.Range(0, fileCount).Select(i => ($"f{i}.txt", "x")).ToArray();
        var form = fileCount == 0 ? new MultipartFormDataContent { { new StringContent("x"), "other" } } : Upload(files);

        var response = await client.PostAsync($"/api/documents/{DocA}", form);

        await AssertRejectedAsync(factory, response, "Exactly one file");
    }

    [Fact]
    public async Task Reingest_InvalidFile_Returns400()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/documents/{DocA}", Upload(("a.pdf", "x")));

        await AssertRejectedAsync(factory, response, "unsupported file type");
    }

    // ---- DELETE /api/documents/{id} ----

    [Fact]
    public async Task Delete_ExistingDocument_Returns204_AndDeletesTheDocument()
    {
        using var factory = NewFactory();
        factory.Store.Setup(s => s.DeleteDocumentAsync(DocA, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/documents/{DocA}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Store.Verify(s => s.DeleteDocumentAsync(DocA, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_UnknownDocument_Returns404()
    {
        using var factory = NewFactory();
        factory.Store.Setup(s => s.DeleteDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/documents/{DocA}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Delete_WhileTheJobIsRunning_CancelsItFirst_ThenDeletes()
    {
        using var factory = NewFactory();
        var order = new List<string>();
        factory.Store.Setup(s => s.DeleteDocumentAsync(DocA, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("deleted")).ReturnsAsync(true);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<IngestJobRegistry>();
        var active = registry.Begin(JobA, DocA, CancellationToken.None);
        // A stand-in worker: lets go of the job once it is cancelled.
        var worker = Task.Run(async () =>
        {
            await Task.Delay(Timeout.Infinite, active.Token).ContinueWith(_ => { });
            order.Add("worker stopped");
            active.Dispose();
        });

        var response = await client.DeleteAsync($"/api/documents/{DocA}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await worker.WaitAsync(TimeSpan.FromSeconds(10));
        active.CancelRequested.Should().BeTrue();
        order.Should().Equal("worker stopped", "deleted");
    }

    [Fact]
    public async Task Delete_WhenTheRunningJobDoesNotStop_Returns409_AndDeletesNothing()
    {
        using var factory = NewFactory(("Rag:Ingest:CancelTimeoutSeconds", "1"));
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<IngestJobRegistry>();
        using var active = registry.Begin(JobA, DocA, CancellationToken.None); // never released

        var response = await client.DeleteAsync($"/api/documents/{DocA}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("did not stop");
        factory.Store.Verify(s => s.DeleteDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OldIngestRoutes_AreGone()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/ingest", Upload(("a.txt", "x")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/ingest/{JobA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
