using System.Collections.Concurrent;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Tests.Support;

/// <summary>
/// A <see cref="SqlIngestQueue"/> on a migrated test database for tests that only need documents to exist. Its
/// bus is built but never started, so the events it publishes just sit in the outbox. One bus per database for
/// the life of the test run (the databases are throwaway).
/// </summary>
public static class TestIngestQueue
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<BusHarness>>> Buses = new();

    public static async Task<SqlIngestQueue> CreateAsync(string connectionString)
    {
        var bus = await Buses.GetOrAdd(connectionString, cs => new Lazy<Task<BusHarness>>(() => BusHarness.CreateAsync(cs))).Value;
        return new SqlIngestQueue(new SqlStoreOptions { ConnectionString = connectionString }, bus.Scopes);
    }
}
