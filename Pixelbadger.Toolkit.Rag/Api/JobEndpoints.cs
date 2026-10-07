using Microsoft.AspNetCore.Http;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;

namespace Pixelbadger.Toolkit.Rag.Api;

public sealed record JobsResponse(IReadOnlyList<IngestJobListItemDto> Jobs, int Page, int PageSize, int TotalCount, int TotalPages);

/// <summary>Read-only view of the ingest queue and job history (never exposes stored bytes).</summary>
public static class JobEndpoints
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/jobs", ListAsync)
            .WithName("ListJobs")
            .Produces<JobsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return routes;
    }

    // Parameters are bound as strings so a malformed value is a ValidationProblem rather than a generic bad-request.
    private static async Task<IResult> ListAsync(string? page, string? pageSize, string? status, IIngestQueue queue, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var pageNumber = DefaultPage;
        if (page is not null && (!int.TryParse(page, out pageNumber) || pageNumber < 1))
            errors["page"] = ["page must be an integer >= 1."];

        var size = DefaultPageSize;
        if (pageSize is not null && (!int.TryParse(pageSize, out size) || size < 1 || size > MaxPageSize))
            errors["pageSize"] = [$"pageSize must be an integer between 1 and {MaxPageSize}."];

        IngestJobStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            // Names only: Enum.TryParse alone would also accept numbers ("2", "99").
            var name = Enum.GetNames<IngestJobStatus>().FirstOrDefault(n => n.Equals(status, StringComparison.OrdinalIgnoreCase));
            if (name is not null)
                statusFilter = Enum.Parse<IngestJobStatus>(name);
            else
                errors["status"] = [$"status must be one of: {string.Join(", ", Enum.GetNames<IngestJobStatus>())}."];
        }

        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var result = await queue.GetJobsAsync(pageNumber, size, statusFilter, cancellationToken);
        var totalPages = (int)Math.Ceiling(result.TotalCount / (double)result.PageSize);
        return Results.Ok(new JobsResponse(result.Jobs, result.Page, result.PageSize, result.TotalCount, totalPages));
    }
}
