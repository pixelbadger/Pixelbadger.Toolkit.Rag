using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Persistence;
using SlimMessageBus;
using SlimMessageBus.Host.Sql.Common;

namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>
/// A SQL transaction shared with the SlimMessageBus outbox: job/document writes made through <see cref="Connection"/>
/// / <see cref="Transaction"/> (or <see cref="CreateDbContext"/>) and the <see cref="JobStatusChanged"/> events
/// published with <see cref="PublishAsync"/> commit or roll back together. Not committing rolls back on dispose.
/// </summary>
/// <remarks>
/// Works because the outbox resolves the scoped <see cref="SqlConnection"/> and <see cref="ISqlTransactionService"/>
/// of the DI scope the event is published from; this class owns that scope. The outbox forwards committed events
/// to the transport (and notifies its sender) once the scope is disposed.
/// </remarks>
public sealed class JobEventTransaction : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly ISqlTransactionService _transactions;
    private readonly IMessageBus _bus;
    private bool _committed;

    private JobEventTransaction(AsyncServiceScope scope, SqlConnection connection, ISqlTransactionService transactions, IMessageBus bus)
    {
        _scope = scope;
        Connection = connection;
        _transactions = transactions;
        _bus = bus;
    }

    public SqlConnection Connection { get; }

    public SqlTransaction Transaction => _transactions.CurrentTransaction
        ?? throw new InvalidOperationException("The transaction has already completed.");

    /// <summary>Opens the scope's connection and begins its transaction (ReadCommitted).</summary>
    public static async Task<JobEventTransaction> BeginAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var scope = scopes.CreateAsyncScope();
        try
        {
            var services = scope.ServiceProvider;
            var connection = services.GetRequiredService<SqlConnection>();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            var transactions = services.GetRequiredService<ISqlTransactionService>();
            await transactions.BeginTransaction();
            return new JobEventTransaction(scope, connection, transactions, services.GetRequiredService<IMessageBus>());
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>A context on this connection, enlisted in this transaction. Dispose it; the connection stays open.</summary>
    public RagDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RagDbContext>().UseSqlServer(Connection).Options;
        var db = new RagDbContext(options);
        db.Database.UseTransaction(Transaction);
        return db;
    }

    /// <summary>Writes the event to the outbox inside this transaction.</summary>
    public Task PublishAsync(Guid jobId, IngestJobStatus status, CancellationToken cancellationToken = default) =>
        _bus.Publish(new JobStatusChanged(jobId, status), cancellationToken: cancellationToken);

    public async Task CommitAsync()
    {
        await _transactions.CommitTransaction();
        _committed = true;
    }

    public async Task RollbackAsync()
    {
        if (_committed)
            return;
        _committed = true;
        await _transactions.RollbackTransaction();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Never committed (exception, early return): roll back. A connection that already died has nothing to undo.
            if (!_committed && _transactions.CurrentTransaction is not null)
            {
                try { await _transactions.RollbackTransaction(); }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException) { }
            }
        }
        finally
        {
            await _scope.DisposeAsync();
        }
    }
}
