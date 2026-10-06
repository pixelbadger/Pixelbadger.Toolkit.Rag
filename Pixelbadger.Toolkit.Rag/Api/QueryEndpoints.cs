using Microsoft.AspNetCore.Http;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Api;

public sealed record QueryRequest(string? Query, int? MaxResults, string[]? SourceIds);

public sealed record QueryResponse(IReadOnlyList<SearchResult> Results);

/// <summary>Hybrid search over REST (the MCP <c>Search</c> tool is the other front door to the same service).</summary>
public static class QueryEndpoints
{
    public const int DefaultMaxResults = 10;

    public static IEndpointRouteBuilder MapQueryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/query", QueryAsync)
            .WithName("Query")
            .Produces<QueryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return routes;
    }

    private static async Task<IResult> QueryAsync(QueryRequest request, ISearchService search, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Query is required."] });
        }

        try
        {
            var sourceIds = request.SourceIds is { Length: > 0 } ? request.SourceIds : null;
            var results = await search.SearchAsync(request.Query, request.MaxResults ?? DefaultMaxResults, sourceIds, cancellationToken);
            return Results.Ok(new QueryResponse(results));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
