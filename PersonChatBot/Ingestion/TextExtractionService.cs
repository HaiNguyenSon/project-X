using PersonChatBot.Models;

namespace PersonChatBot.Ingestion;

/// <summary>Dispatches a file to the first registered extractor that handles its extension.</summary>
public sealed class TextExtractionService
{
    private readonly IReadOnlyList<ITextExtractor> _extractors;

    public TextExtractionService(IEnumerable<ITextExtractor> extractors)
        => _extractors = extractors.ToList();

    public bool IsSupported(string filePath)
        => _extractors.Any(e => e.CanHandle(GetExtension(filePath)));

    public async Task<IReadOnlyList<ExtractedPage>> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        var extension = GetExtension(filePath);
        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(extension))
            ?? throw new NotSupportedException($"No extractor registered for '{extension}'.");
        return await extractor.ExtractAsync(filePath, ct);
    }

    private static string GetExtension(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant();
}
