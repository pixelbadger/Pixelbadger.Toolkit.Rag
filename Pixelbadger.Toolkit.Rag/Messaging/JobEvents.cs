namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>Names of the bus topology (SlimMessageBus SQL transport, same database as the app).</summary>
public static class JobEvents
{
    /// <summary>Topic every <see cref="JobStatusChanged"/> is published to.</summary>
    public const string Topic = "job-status-changed";

    /// <summary>Subscription that runs ingest jobs (acts on Queued).</summary>
    public const string IngestSubscription = "ingest";

    /// <summary>Subscription that maintains the vector index (acts on terminal statuses).</summary>
    public const string VectorIndexSubscription = "vector-index";

    /// <summary>Transport message table (and, suffixed, its subscriptions table).</summary>
    public const string MessagesTable = "BusMessages";

    /// <summary>Migrations table of the transport.</summary>
    public const string MessagesMigrationsTable = "BusMessagesMigrations";

    /// <summary>Outbox table (and its migrations table).</summary>
    public const string OutboxTable = "BusOutbox";

    public const string OutboxMigrationsTable = "BusOutboxMigrations";
}
