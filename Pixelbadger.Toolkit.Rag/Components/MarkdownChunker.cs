using System.Text;
using System.Text.RegularExpressions;

namespace Pixelbadger.Toolkit.Rag.Components;

public class MarkdownChunk
{
    public string Content { get; set; } = string.Empty;
    public string HeaderText { get; set; } = string.Empty;
    public int HeaderLevel { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

public static class MarkdownChunker
{
    private static readonly Regex HeaderRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline);

    public static List<MarkdownChunk> ChunkByHeaders(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<MarkdownChunk>();
        }

        var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var chunks = new List<MarkdownChunk>();

        var currentChunk = new StringBuilder();
        var currentHeaderText = string.Empty;
        var currentHeaderLevel = 0;
        var currentStartLine = 1;
        var lineNumber = 1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var headerMatch = HeaderRegex.Match(line);

            if (headerMatch.Success)
            {
                // If we have content in the current chunk, save it
                if (currentChunk.Length > 0)
                {
                    var chunkContent = currentChunk.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(chunkContent))
                    {
                        chunks.Add(new MarkdownChunk
                        {
                            Content = chunkContent,
                            HeaderText = currentHeaderText,
                            HeaderLevel = currentHeaderLevel,
                            StartLine = currentStartLine,
                            EndLine = lineNumber - 1
                        });
                    }
                }

                // Start a new chunk with the header
                currentChunk.Clear();
                currentChunk.AppendLine(line);
                currentHeaderText = headerMatch.Groups[2].Value.Trim();
                currentHeaderLevel = headerMatch.Groups[1].Length;
                currentStartLine = lineNumber;
            }
            else
            {
                // Add non-header line to current chunk
                currentChunk.AppendLine(line);
            }

            lineNumber++;
        }

        // Don't forget the last chunk
        if (currentChunk.Length > 0)
        {
            var chunkContent = currentChunk.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(chunkContent))
            {
                chunks.Add(new MarkdownChunk
                {
                    Content = chunkContent,
                    HeaderText = currentHeaderText,
                    HeaderLevel = currentHeaderLevel,
                    StartLine = currentStartLine,
                    EndLine = lineNumber - 1
                });
            }
        }

        // Handle content before the first header
        if (chunks.Count > 0 && chunks[0].StartLine > 1)
        {
            var preHeaderLines = lines.Take(chunks[0].StartLine - 1);
            var preHeaderContent = string.Join("\n", preHeaderLines).Trim();

            if (!string.IsNullOrWhiteSpace(preHeaderContent))
            {
                chunks.Insert(0, new MarkdownChunk
                {
                    Content = preHeaderContent,
                    HeaderText = string.Empty,
                    HeaderLevel = 0,
                    StartLine = 1,
                    EndLine = chunks[0].StartLine - 1
                });
            }
        }
        else if (chunks.Count == 0 && lines.Length > 0)
        {
            // No headers found, treat entire content as one chunk
            var allContent = string.Join("\n", lines).Trim();
            if (!string.IsNullOrWhiteSpace(allContent))
            {
                chunks.Add(new MarkdownChunk
                {
                    Content = allContent,
                    HeaderText = string.Empty,
                    HeaderLevel = 0,
                    StartLine = 1,
                    EndLine = lines.Length
                });
            }
        }

        return chunks;
    }
}
