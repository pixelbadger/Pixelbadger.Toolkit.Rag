using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using SlimMessageBus;

namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>The <c>vector-index</c> subscription: a job finished -> maybe build the index.</summary>
public sealed class VectorIndexConsumer(IVectorIndexService service) : IConsumer<JobStatusChanged>
{
    public Task OnHandle(JobStatusChanged message, CancellationToken cancellationToken) =>
        IngestJob.IsTerminal(message.Status)
            ? service.OnJobFinishedAsync(cancellationToken)
            : Task.CompletedTask;
}
