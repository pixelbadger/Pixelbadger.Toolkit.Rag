using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Aspire service defaults, trimmed to OpenTelemetry (logs, metrics, traces). The app makes no outbound HTTP calls,
/// so there is no service discovery or HTTP resilience, and it maps its own <c>/health</c> endpoint.
/// Telemetry is exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (the AppHost sets it,
/// locally and in Azure Container Apps); otherwise nothing is exported.
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(o =>
                        // Health probes are noise in traces.
                        o.Filter = context => !context.Request.Path.StartsWithSegments(HealthEndpointPath));
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
    }
}
