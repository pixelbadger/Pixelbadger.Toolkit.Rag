using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pixelbadger.Toolkit.Rag.Messaging;
using Pixelbadger.Toolkit.Rag.Persistence;
using SlimMessageBus.Host;

namespace Pixelbadger.Toolkit.Rag.Tests.Messaging;

/// <summary>
/// The startup service against an unreachable database: starting must not block the host, and stopping before SQL
/// answers must never build the bus or stop the application. (The bus itself is covered by the SQL-backed tests.)
/// </summary>
public class MessageBusStartupServiceTests : IDisposable
{
    private const string UnreachableConnectionString = "Server=nonexistent.invalid;Database=x;Connect Timeout=1";

    private readonly ServiceProvider _provider;
    private readonly Mock<IHostApplicationLifetime> _lifetime = new();
    private readonly MessageBusStartupService _sut;
    private bool _busResolved;

    public MessageBusStartupServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConsumerControl>(_ =>
        {
            _busResolved = true;
            return Mock.Of<IConsumerControl>();
        });
        _provider = services.BuildServiceProvider();
        _sut = new MessageBusStartupService(
            _provider,
            new SqlStoreOptions { ConnectionString = UnreachableConnectionString },
            _lifetime.Object,
            NullLogger<MessageBusStartupService>.Instance);
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task StartAsync_ReturnsImmediately_EvenWhenSqlIsUnreachable()
    {
        var clock = Stopwatch.StartNew();

        await _sut.StartAsync(CancellationToken.None);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        await _sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_BeforeSqlAnswers_NeverBuildsTheBus_AndDoesNotStopTheApplication()
    {
        await _sut.StartAsync(CancellationToken.None);

        await _sut.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        _busResolved.Should().BeFalse("the bus must not be built while the database is unreachable");
        _lifetime.Verify(l => l.StopApplication(), Times.Never);
    }
}
