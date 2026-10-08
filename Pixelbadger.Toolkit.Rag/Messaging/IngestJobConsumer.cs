using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using SlimMessageBus;

namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>The <c>ingest</c> subscription: a job became Queued -> run it.</summary>
public sealed class IngestJobConsumer(IIngestJobService service) : IConsumer<JobStatusChanged>
{
    public Task OnHandle(JobStatusChanged message, CancellationToken cancellationToken) =>
        message.Status == IngestJobStatus.Queued
            ? service.ProcessAsync(message.JobId, cancellationToken)
            : Task.CompletedTask;
}
