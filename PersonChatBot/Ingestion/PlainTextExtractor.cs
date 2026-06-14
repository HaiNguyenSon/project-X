using PersonChatBot.Models;

namespace PersonChatBot.Ingestion;

/// <summary>Reads .txt and .md files as-is. No page concept, so PageNumber is 0.</summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension is ".txt" or ".md";

    public async Task<IReadOnlyList<ExtractedPage>> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(filePath, ct);
        return string.IsNullOrWhiteSpace(text)
            ? []
            : new List<ExtractedPage> { new(0, text) };
    }
}
