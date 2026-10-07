using Microsoft.Extensions.Logging;
using SlimMessageBus.Host;
using SlimMessageBus.Host.Consumer;
using SlimMessageBus.Host.Interceptor;
using SlimMessageBus.Host.Sql;

namespace Pixelbadger.Toolkit.Rag.Messaging;

/// <summary>
/// <see cref="SqlMessageBus"/> whose consumers stop cleanly. In SlimMessageBus 3.5 stopping cancels the consumer's
/// in-flight SQL call; SqlClient reports that as a <c>SqlException</c> ("Operation cancelled by user") (or the retry
/// helper throws a <c>NullReferenceException</c> when the token is already cancelled), the poll loop rethrows it, and
/// the consumer never records that it stopped, so every later Stop/Dispose throws again.
/// </summary>
/// <remarks>
/// Nothing is lost by ignoring it: a message whose completion was interrupted keeps its lock, is redelivered when the
/// lock expires and is a no-op for the services (its job is no longer Queued/Processing).
/// </remarks>
internal sealed class QuietStopSqlMessageBus(MessageBusSettings settings, SqlMessageBusSettings providerSettings)
    : SqlMessageBus(settings, providerSettings)
{
    protected override AbstractConsumer CreateConsumer(
        IEnumerable<AbstractConsumerSettings> consumerSettings,
        IMessageProcessor<SqlTransportMessage> processor,
        string path,
        PathKind pathKind,
        string subscriptionName,
        string instanceId)
        => new QuietStopSqlConsumer(
            LoggerFactory.CreateLogger<SqlConsumer>(),
            consumerSettings,
            Settings.ServiceProvider.GetServices<IAbstractConsumerInterceptor>(),
            Settings.ServiceProvider,
            ProviderSettings,
            processor,
            path,
            pathKind,
            subscriptionName,
            instanceId);

    private sealed class QuietStopSqlConsumer(
        ILogger<SqlConsumer> logger,
        IEnumerable<AbstractConsumerSettings> consumerSettings,
        IEnumerable<IAbstractConsumerInterceptor> interceptors,
        IServiceProvider serviceProvider,
        SqlMessageBusSettings providerSettings,
        IMessageProcessor<SqlTransportMessage> messageProcessor,
        string path,
        PathKind pathKind,
        string subscriptionName,
        string instanceId)
        : SqlConsumer(logger, consumerSettings, interceptors, serviceProvider, providerSettings, messageProcessor, path, pathKind, subscriptionName, instanceId)
    {
        private readonly ILogger _logger = logger;

        protected override async Task OnStop()
        {
            try
            {
                await base.OnStop();
            }
            catch (Exception ex) when (CancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "The poll of {Path} was cancelled while stopping", Path);
            }
        }
    }
}
