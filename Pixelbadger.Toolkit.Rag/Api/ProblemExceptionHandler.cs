using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Pixelbadger.Toolkit.Rag.Api;

/// <summary>
/// Turns unhandled exceptions into ProblemDetails with a generic message. The exception itself is logged by the
/// exception-handler middleware and never reaches the client.
/// </summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Malformed/oversized requests carry their own status code (400, 413...).
        var status = exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        httpContext.Response.StatusCode = status;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = status,
                Title = status == StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : "The request could not be processed."
            }
        });
    }
}
