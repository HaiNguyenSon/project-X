using PersonChatBot.Models;

namespace PersonChatBot.Ingestion;

/// <summary>Extracts plain text (with page structure where available) from one file type.</summary>
public interface ITextExtractor
{
    /// <summary>True if this extractor handles the given lower-cased extension (e.g. ".pdf").</summary>
    bool CanHandle(string extension);

    Task<IReadOnlyList<ExtractedPage>> ExtractAsync(string filePath, CancellationToken ct = default);
}
