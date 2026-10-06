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

    /// <summary>Times a job may be claimed before it is marked Failed.</summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    public int LeaseSeconds { get; set; } = DefaultLeaseSeconds;

    public int PollIntervalSeconds { get; set; } = DefaultPollIntervalSeconds;

    /// <summary>How long deleting a document waits for its running ingest job to stop before giving up (409).</summary>
    public int CancelTimeoutSeconds { get; set; } = DefaultCancelTimeoutSeconds;

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public TimeSpan CancelTimeout => TimeSpan.FromSeconds(CancelTimeoutSeconds);

    /// <summary>Upper bound for a whole multipart request body (all files plus form overhead).</summary>
    public long MaxRequestBodyBytes => checked(MaxFilesPerRequest * MaxFileSizeBytes + 1024 * 1024);
}
