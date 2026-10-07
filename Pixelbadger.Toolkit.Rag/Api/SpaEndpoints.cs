using Microsoft.AspNetCore.Routing;

namespace Pixelbadger.Toolkit.Rag.Api;

/// <summary>
/// Hosts the built React app (copied into <c>wwwroot</c> at container build time) from the same origin as the API.
/// <c>wwwroot</c> may be absent or empty (dev without a built UI, tests): nothing fails, the UI routes answer 404.
/// </summary>
public static class SpaEndpoints
{
    private const string IndexFile = "index.html";

    private static readonly string[] ReservedPrefixes = ["/api", "/mcp", "/health"];

    /// <summary>Serves wwwroot files (and index.html at <c>/</c>). Call before the endpoint maps.</summary>
    public static WebApplication UseSpaStaticFiles(this WebApplication app)
    {
        // Both middlewares no-op when the web root is missing (NullFileProvider).
        app.UseDefaultFiles();
        app.UseStaticFiles();
        return app;
    }

    /// <summary>
    /// Browser navigations (GET/HEAD) to unknown, extension-less paths get index.html so client-side routes survive a
    /// refresh. Lowest-priority endpoint: real endpoints win, and <c>/api/*</c>, <c>/mcp</c>, <c>/health</c> and
    /// missing files stay 404. Non-GET methods are left alone so API 405 responses are unchanged. Call after the maps.
    /// </summary>
    public static WebApplication MapSpaFallback(this WebApplication app)
    {
        app.MapFallback(async (HttpContext context, IWebHostEnvironment env) =>
        {
            var path = context.Request.Path;
            if (IsReserved(path) || Path.HasExtension(path.Value))
                return Results.NotFound();

            var index = env.WebRootFileProvider.GetFileInfo(IndexFile);
            if (!index.Exists)
                return Results.NotFound();

            // No-cache so a new deployment's hashed assets are picked up on the next navigation.
            context.Response.Headers.CacheControl = "no-cache";
            return Results.File(index.CreateReadStream(), "text/html; charset=utf-8");
        }).WithMetadata(new HttpMethodMetadata(["GET", "HEAD"]));
        return app;
    }

    private static bool IsReserved(PathString path) =>
        ReservedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
}
