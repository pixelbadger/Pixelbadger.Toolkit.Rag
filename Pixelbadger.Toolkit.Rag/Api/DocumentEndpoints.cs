using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Api;

public sealed record DocumentsResponse(IReadOnlyList<DocumentDto> Documents);

/// <summary>
/// Document-centric upload endpoints: create documents (batch), re-ingest one, read one, delete one. Uploads are
/// queued and processed in the background; the server never reads caller-named paths from its own disk.
/// </summary>
public static class DocumentEndpoints
{
    public const string FilesField = IngestRequestValidator.FilesField;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/documents", CreateAsync)
            .WithName("CreateDocuments")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<DocumentsResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            // Antiforgery protects cookie-authenticated browser forms; this API has neither cookies nor auth.
            .DisableAntiforgery();

        routes.MapGet("/api/documents/{documentId:guid}", GetAsync)
            .WithName("GetDocument")
            .Produces<DocumentDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapGet("/api/documents/{documentId:guid}/content", GetContentAsync)
            .WithName("GetDocumentContent")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound);

        // POST, not PUT: it queues a job (or replaces a queued one) rather than being an idempotent replace.
        routes.MapPost("/api/documents/{documentId:guid}", ReingestAsync)
            .WithName("ReingestDocument")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<DocumentDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .DisableAntiforgery();

        routes.MapDelete("/api/documents/{documentId:guid}", DeleteAsync)
            .WithName("DeleteDocument")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<IResult> CreateAsync(
        HttpRequest request,
        IngestSettings settings,
        IngestRequestValidator validator,
        IIngestQueue queue,
        CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(request, settings, cancellationToken);
        if (form.Failure is not null)
            return form.Failure;

        var upload = Validate(form.Form!, validator, exactlyOne: false);
        if (upload.Failure is not null)
            return upload.Failure;

        var created = await queue.EnqueueNewDocumentsAsync(upload.Uploads, upload.MaxChunkCharacters, cancellationToken);

        var documents = new List<DocumentDto>(created.Count);
        foreach (var item in created)
        {
            // The document row exists as soon as the upload is accepted, so this is never null here.
            documents.Add((await queue.GetDocumentAsync(item.DocumentId, cancellationToken))!);
        }

        // A Location only makes sense for a single resource.
        var location = documents.Count == 1 ? DocumentLocation(documents[0].DocumentId) : null;
        return Results.Accepted(location, new DocumentsResponse(documents));
    }

    private static async Task<IResult> GetAsync(Guid documentId, IIngestQueue queue, CancellationToken cancellationToken)
    {
        var document = await queue.GetDocumentAsync(documentId, cancellationToken);
        return document is null ? NotFound(documentId) : Results.Ok(document);
    }

    /// <summary>
    /// The canonical source of the document's latest successfully indexed version, straight from SQL (never from a
    /// path on disk). Inline by default so browsers display it; <c>?download=true</c> asks for an attachment.
    /// </summary>
    private static async Task<IResult> GetContentAsync(
        Guid documentId,
        bool? download,
        HttpResponse response,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        var content = await store.GetContentAsync(documentId, cancellationToken);
        if (content is null)
        {
            return Results.Problem(
                $"Document '{documentId}' was not found or has no indexed source content yet.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Uploaded bytes are served from the app's own origin: never let a browser second-guess the media type.
        response.Headers.XContentTypeOptions = "nosniff";

        if (download == true)
            return Results.File(content.Bytes, content.ContentType, content.FileName, enableRangeProcessing: true);

        // Results.File only produces attachments; the header value class handles quoting and non-ASCII names.
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(content.FileName);
        response.Headers.ContentDisposition = disposition.ToString();
        return Results.File(content.Bytes, content.ContentType, enableRangeProcessing: true);
    }

    private static async Task<IResult> ReingestAsync(
        Guid documentId,
        HttpRequest request,
        IngestSettings settings,
        IngestRequestValidator validator,
        IIngestQueue queue,
        CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(request, settings, cancellationToken);
        if (form.Failure is not null)
            return form.Failure;

        var upload = Validate(form.Form!, validator, exactlyOne: true);
        if (upload.Failure is not null)
            return upload.Failure;

        var result = await queue.EnqueueReingestAsync(documentId, upload.Uploads[0], upload.MaxChunkCharacters, cancellationToken);
        switch (result.Outcome)
        {
            case ReingestOutcome.NotFound:
                return NotFound(documentId);
            case ReingestOutcome.Conflict:
                return Results.Problem(
                    $"Document '{documentId}' is being processed right now, so a new version cannot be accepted. " +
                    "Wait for the current job to finish (GET the document to see its status) and upload again.",
                    statusCode: StatusCodes.Status409Conflict);
        }

        var document = await queue.GetDocumentAsync(documentId, cancellationToken);
        return document is null
            ? NotFound(documentId) // deleted right after it was queued
            : Results.Accepted(DocumentLocation(documentId), document);
    }

    private static async Task<IResult> DeleteAsync(Guid documentId, DocumentService documents, CancellationToken cancellationToken)
    {
        return await documents.DeleteAsync(documentId, cancellationToken) switch
        {
            DeleteOutcome.Deleted => Results.NoContent(),
            DeleteOutcome.NotFound => NotFound(documentId),
            _ => Results.Problem(
                $"The running ingest job of document '{documentId}' did not stop in time, so the document was not deleted. Try again shortly.",
                statusCode: StatusCodes.Status409Conflict)
        };
    }

    private static string DocumentLocation(Guid documentId) => $"/api/documents/{documentId}";

    private static IResult NotFound(Guid documentId) =>
        Results.Problem($"Document '{documentId}' was not found.", statusCode: StatusCodes.Status404NotFound);

    private sealed record FormResult(IFormCollection? Form, IResult? Failure);

    private static async Task<FormResult> ReadFormAsync(HttpRequest request, IngestSettings settings, CancellationToken cancellationToken)
    {
        // Only the upload endpoints accept large bodies (MaxFilesPerRequest x MaxFileSizeBytes + slack); Kestrel's default applies elsewhere.
        var bodyLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
            bodyLimit.MaxRequestBodySize = settings.MaxRequestBodyBytes;

        try
        {
            return new FormResult(await request.ReadFormAsync(cancellationToken), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status400BadRequest })
        {
            // Malformed multipart, or a part larger than the per-file limit (FormOptions.MultipartBodyLengthLimit).
            return new FormResult(null, Results.Problem(
                $"The multipart body could not be read: {ex.Message} Each file may be at most {settings.MaxFileSizeBytes} bytes.",
                statusCode: StatusCodes.Status400BadRequest));
        }
    }

    private sealed record UploadResult(IReadOnlyList<IngestUpload> Uploads, int MaxChunkCharacters, IResult? Failure);

    private static UploadResult Validate(IFormCollection form, IngestRequestValidator validator, bool exactlyOne)
    {
        var files = form.Files.GetFiles(FilesField);
        if (exactlyOne && files.Count != 1)
        {
            return new UploadResult([], 0, Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [FilesField] = [$"Exactly one file is required (one multipart part named '{FilesField}'), but {files.Count} were uploaded."]
            }));
        }

        var validation = validator.Validate(
            files.Select(f => new IngestFileCandidate(f.FileName, f.Length)).ToList(),
            form[IngestRequestValidator.MaxChunkCharactersField].FirstOrDefault());
        if (!validation.IsValid)
            return new UploadResult([], 0, Results.ValidationProblem(validation.Errors.ToDictionary(e => e.Key, e => e.Value)));

        var uploads = files
            .Select((file, i) => new IngestUpload(validation.Paths[i], file.Length, () => file.OpenReadStream()))
            .ToList();
        return new UploadResult(uploads, validation.MaxChunkCharacters, null);
    }
}
