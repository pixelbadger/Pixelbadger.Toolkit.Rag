using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pixelbadger.Toolkit.Rag.Ingestion;
using SlimMessageBus.Host;
using SlimMessageBus.Host.Outbox;
using SlimMessageBus.Host.Outbox.Sql;
using SlimMessageBus.Host.Serialization.SystemTextJson;
using SlimMessageBus.Host.Sql;

namespace Pixelbadger.Toolkit.Rag.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// SlimMessageBus on the app's own database: <see cref="JobStatusChanged"/> is published through the SQL outbox
    /// (atomic with the job write, see <see cref="JobEventTransaction"/>) to the <see cref="JobEvents.Topic"/> topic of
    /// the SQL transport, whose two subscriptions run ingest jobs and maintain the vector index, in process.
    /// </summary>
    /// <remarks>
    /// Consumers do not start on their own: <see cref="MessageBusStartupService"/> (a hosted service registered after
    /// the migrations) starts them, so nothing is consumed before the schema exists. Building the bus provisions its
    /// tables (BusMessages*, BusOutbox*) on first use; nothing touches SQL at registration or construction.
    /// Single instance: one consumer per subscription, one message at a time (PollBatchSize 1). Message locks are not
    /// renewed, so <see cref="IngestSettings.Lease"/> (the lock duration) must exceed the longest ingest; a job whose
    /// process died is redelivered once its lock expires.
    /// </remarks>
    public static IServiceCollection AddRagMessaging(this IServiceCollection services, RagOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = options.Sql.ConnectionString;
        var ingest = options.Ingest;

        services.AddSlimMessageBus(mbb => mbb
            .WithProviderSql(cfg =>
            {
                cfg.ConnectionString = connectionString;
                cfg.DatabaseTableName = JobEvents.MessagesTable;
                cfg.DatabaseMigrationsTableName = JobEvents.MessagesMigrationsTable;
                cfg.PollDelay = ingest.PollInterval;
                cfg.PollBatchSize = 1;
                cfg.LockDuration = ingest.Lease;
                // Backstop only: the ingest service fails the job itself after Rag:Ingest:MaxAttempts.
                cfg.MaxDeliveryAttempts = ingest.MaxAttempts + 2;
            })
            .AddJsonSerializer()
            .AutoStartConsumersEnabled(false)
            .Produce<JobStatusChanged>(x => x.DefaultTopic(JobEvents.Topic).ToTopic().UseOutbox())
            .Consume<JobStatusChanged>(x => x
                .Topic(JobEvents.Topic, JobEvents.IngestSubscription)
                .WithConsumer<IngestJobConsumer>())
            .Consume<JobStatusChanged>(x => x
                .Topic(JobEvents.Topic, JobEvents.VectorIndexSubscription)
                .WithConsumer<VectorIndexConsumer>())
            .AddOutboxUsingSql(o =>
            {
                o.SqlSettings.DatabaseTableName = JobEvents.OutboxTable;
                o.SqlSettings.DatabaseMigrationsTableName = JobEvents.OutboxMigrationsTable;
                // Publishing notifies the sender at once; this only bounds the delay after a missed notification.
                o.PollIdleSleep = ingest.PollInterval;
            }));

        // MessageBusStartupService starts and stops the bus after the migrations; SlimMessageBus's own hosted service
        // would build the bus (and begin provisioning its tables) at host start, before them.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IHostedService)
                && services[i].ImplementationType?.FullName == "SlimMessageBus.Host.MessageBusHostedService")
                services.RemoveAt(i);
        }

        services.AddTransient<IngestJobConsumer>();
        services.AddTransient<VectorIndexConsumer>();
        return services;
    }
}
