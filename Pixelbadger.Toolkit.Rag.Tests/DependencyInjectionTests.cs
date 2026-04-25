using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Commands;
using Pixelbadger.Toolkit.Rag.Components;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddRagServices_ShouldResolveBm25OnlyCommands_WhenOpenAiApiKeyIsMissing()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

            var services = new ServiceCollection();
            services.AddRagServices();

            using var serviceProvider = services.BuildServiceProvider();

            serviceProvider.GetRequiredService<IngestCommand>().Should().NotBeNull();
            serviceProvider.GetRequiredService<QueryCommand>().Should().NotBeNull();
            serviceProvider.GetRequiredService<ServeCommand>().Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalApiKey);
        }
    }
}
