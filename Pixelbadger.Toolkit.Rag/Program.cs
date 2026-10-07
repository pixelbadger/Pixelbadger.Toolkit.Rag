using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Pixelbadger.Toolkit.Rag;
using Pixelbadger.Toolkit.Rag.Api;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Configuration;
using Pixelbadger.Toolkit.Rag.Mcp;

var builder = WebApplication.CreateBuilder(args);

RagOptions rag;
try
{
    rag = RagConfiguration.Bind(builder.Configuration);
}
catch (RagConfigurationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

// OpenTelemetry, exported over OTLP when the Aspire AppHost (locally or in Azure) supplies an endpoint.
builder.AddServiceDefaults();

builder.Services.AddRagServices(rag).AddRagHostedServices();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<FormOptions>(o =>
{
    // MultipartBodyLengthLimit applies per multipart section, i.e. per uploaded file.
    o.MultipartBodyLengthLimit = rag.Ingest.MaxFileSizeBytes;
    o.ValueCountLimit = rag.Ingest.MaxFilesPerRequest + 16;
});

// Search tool only; ingest is REST-only. Stateless: every request is independent, no session affinity.
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<McpRagServer>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health");
app.MapDocumentEndpoints();
app.MapQueryEndpoints();
app.MapMcp("/mcp");

app.Run();
return 0;

/// <summary>Entry point marker so tests can host the app with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
