using Microsoft.Data.SqlClient;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>Removes delivered bus messages (the SlimMessageBus SQL transport keeps them; the outbox cleans up itself).</summary>
public interface IDeliveredMessageCleanup
{
    /// <summary>Deletes transport messages delivered more than <see cref="DeliveredMessageCleanup.Retention"/> ago; returns how many.</summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);
}

public sealed class DeliveredMessageCleanup(SqlStoreOptions sql) : IDeliveredMessageCleanup
{
    /// <summary>Delivered messages are kept this long (diagnostics); aborted ones are never removed.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    private const int BatchSize = 5000;

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var total = 0;
        while (true)
        {
            // Small batches keep the locks short; the consumers' poll skips locked rows (READPAST) anyway.
            await using var command = new SqlCommand(
                $"DELETE TOP (@batch) FROM dbo.{JobEvents.MessagesTable} WHERE DeliveryComplete = 1 AND CreatedOn < DATEADD(SECOND, -@retention, SYSUTCDATETIME())",
                connection);
            command.Parameters.AddWithValue("@batch", BatchSize);
            command.Parameters.AddWithValue("@retention", (int)Retention.TotalSeconds);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
                return total;
        }
    }
}
