using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Pixelbadger.Toolkit.Rag.Ingestion;

namespace Pixelbadger.Toolkit.Rag.Api;

/// <summary>Upload-and-enqueue ingest endpoints. The server never reads caller-named paths from its own disk.</summary>
public static class IngestEndpoints
{
    public const string FilesField = IngestRequestValidator.FilesField;

    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/ingest", EnqueueAsync)
            .WithName("EnqueueIngest")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<IngestJobStatusDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            // Antiforgery protects cookie-authenticated browser forms; this API has neither cookies nor auth.
            .DisableAntiforgery();

        routes.MapGet("/api/ingest/{jobId:guid}", GetAsync)
            .WithName("GetIngestJob")
            .Produces<IngestJobStatusDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<IResult> EnqueueAsync(
        HttpRequest request,
        IngestSettings settings,
        IngestRequestValidator validator,
        IIngestQueue queue,
        IngestWorkerSignal signal,
        CancellationToken cancellationToken)
    {
        // Only this endpoint accepts large bodies (MaxFilesPerJob x MaxFileSizeBytes + slack); Kestrel's default applies elsewhere.
        var bodyLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
            bodyLimit.MaxRequestBodySize = settings.MaxRequestBodyBytes;

        if (!request.HasFormContentType)
            return Results.Problem("The request must be multipart/form-data.", statusCode: StatusCodes.Status400BadRequest);

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status400BadRequest })
        {
            // Malformed multipart, or a part larger than the per-file limit.
            return Results.Problem($"The multipart body could not be read: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
        }

        var files = form.Files.GetFiles(FilesField);
        var validation = validator.Validate(
            files.Select(f => new IngestFileCandidate(f.FileName, f.Length)).ToList(),
            form[IngestRequestValidator.MaxChunkCharactersField].FirstOrDefault());
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.Errors.ToDictionary(e => e.Key, e => e.Value));

        var uploads = files
            .Select((file, i) => new IngestUpload(validation.Paths[i], file.Length, () => file.OpenReadStream()))
            .ToList();
        var jobId = await queue.EnqueueAsync(uploads, validation.MaxChunkCharacters, cancellationToken);
        signal.Notify();

        var status = await queue.GetJobAsync(jobId, cancellationToken);
        return Results.Accepted($"/api/ingest/{jobId}", status);
    }

    private static async Task<IResult> GetAsync(Guid jobId, IIngestQueue queue, CancellationToken cancellationToken)
    {
        var status = await queue.GetJobAsync(jobId, cancellationToken);
        return status is null
            ? Results.Problem($"Ingest job '{jobId}' was not found.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(status);
    }
}
