using FluentAssertions;
using System.CommandLine;
using Pixelbadger.Toolkit.Rag.Commands;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Dtos;

namespace Pixelbadger.Toolkit.Rag.Tests;

public class IngestCommandTests : IDisposable
{
    private readonly string _testDirectory;

    public IngestCommandTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public async Task IngestCommand_ShouldPassSecurityOptionsToIngester()
    {
        var contentFile = Path.Combine(_testDirectory, "content.txt");
        await File.WriteAllTextAsync(contentFile, "content");

        var ingester = new RecordingIngester();
        var command = new IngestCommand(ingester).Create();

        var exitCode = await command.InvokeAsync(new[]
        {
            "--index-path", Path.Combine(_testDirectory, "index"),
            "--content-path", contentFile,
            "--no-vectors",
            "--max-file-size-bytes", "123",
            "--max-files", "7",
            "--max-chunk-characters", "456",
            "--allow-symlinks"
        });

        exitCode.Should().Be(0);
        ingester.Options.Should().NotBeNull();
        ingester.Options!.EnableVectorStorage.Should().BeFalse();
        ingester.Options.MaxFileSizeBytes.Should().Be(123);
        ingester.Options.MaxFiles.Should().Be(7);
        ingester.Options.MaxChunkCharacters.Should().Be(456);
        ingester.Options.AllowSymlinks.Should().BeTrue();
    }

    private sealed class RecordingIngester : IContentIngester
    {
        public IngestOptions? Options { get; private set; }

        public Task IngestContentAsync(string indexPath, string contentPath)
        {
            return IngestContentAsync(indexPath, contentPath, null);
        }

        public Task IngestContentAsync(string indexPath, string contentPath, IngestOptions? options)
        {
            Options = options;
            return Task.CompletedTask;
        }

        public Task IngestFolderAsync(string indexPath, string folderPath, IngestOptions? options = null)
        {
            Options = options;
            return Task.CompletedTask;
        }
    }
}
