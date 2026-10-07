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
    /// Browser navigations (GET/HEAD) that no endpoint matched, with an extension-less path outside <c>/api</c>,
    /// <c>/mcp</c> and <c>/health</c>, get index.html so client-side routes survive a refresh. Implemented as
    /// middleware that only acts when routing found no endpoint at all, so real endpoints and their own 404/405/415
    /// answers are untouched; unknown API routes and missing files stay 404. Call after <see cref="UseSpaStaticFiles"/>.
    /// </summary>
    public static WebApplication UseSpaFallback(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (context.GetEndpoint() is null
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && !IsReserved(path) && !Path.HasExtension(path.Value))
            {
                var index = context.RequestServices.GetRequiredService<IWebHostEnvironment>()
                    .WebRootFileProvider.GetFileInfo(IndexFile);
                if (index.Exists)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    // No-cache so a new deployment's hashed assets are picked up on the next navigation.
                    context.Response.Headers.CacheControl = "no-cache";
                    context.Response.ContentLength = index.Length;
                    if (HttpMethods.IsGet(context.Request.Method))
                        await context.Response.SendFileAsync(index);
                    return;
                }
            }

            await next();
        });
        return app;
    }

    private static bool IsReserved(PathString path) =>
        ReservedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
}
