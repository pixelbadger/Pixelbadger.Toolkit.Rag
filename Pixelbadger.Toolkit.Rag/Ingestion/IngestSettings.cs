using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>Limits and timings of the upload + background-job ingest pipeline (the <c>Rag:Ingest</c> section).</summary>
public sealed class IngestSettings
{
    public const int DefaultMaxFilesPerRequest = 100;
    public const int DefaultMaxAttempts = 3;
    public const int DefaultLeaseSeconds = 600;
    public const int DefaultPollIntervalSeconds = 2;
    public const int DefaultCancelTimeoutSeconds = 30;

    /// <summary>Maximum size of a single uploaded file.</summary>
    public long MaxFileSizeBytes { get; set; } = IngestOptions.DefaultMaxFileSizeBytes;

    /// <summary>Most files one <c>POST /api/documents</c> request may carry (each becomes its own document and job).</summary>
    public int MaxFilesPerRequest { get; set; } = DefaultMaxFilesPerRequest;

    /// <summary>Upper bound for a text chunk; a request may only lower it.</summary>
    public int MaxChunkCharacters { get; set; } = IngestOptions.DefaultMaxChunkCharacters;

    /// <summary>
    /// Attempts per job (one bus delivery each) before it is marked Failed. The bus's own delivery limit is this value
    /// plus 2 and only a backstop: the ingest service fails the job itself.
    /// </summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    /// <summary>
    /// Bus message lock duration: how long before an ingest job whose process died (crash, kill) is delivered again.
    /// Locks are not renewed, so this must exceed the longest ingest.
    /// </summary>
    public int LeaseSeconds { get; set; } = DefaultLeaseSeconds;

    /// <summary>
    /// How often the bus consumers poll the message table when idle, and the outbox sender's idle sleep (a bound on
    /// the delay after a missed notification; publishing notifies the sender at once).
    /// </summary>
    public int PollIntervalSeconds { get; set; } = DefaultPollIntervalSeconds;

    /// <summary>How long deleting a document waits for its running ingest job to stop before giving up (409).</summary>
    public int CancelTimeoutSeconds { get; set; } = DefaultCancelTimeoutSeconds;

    /// <summary>The bus message lock duration (<see cref="LeaseSeconds"/>).</summary>
    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    /// <summary>The bus idle poll interval and outbox idle sleep (<see cref="PollIntervalSeconds"/>).</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public TimeSpan CancelTimeout => TimeSpan.FromSeconds(CancelTimeoutSeconds);

    /// <summary>Upper bound for a whole multipart request body (all files plus form overhead).</summary>
    public long MaxRequestBodyBytes => checked(MaxFilesPerRequest * MaxFileSizeBytes + 1024 * 1024);
}
