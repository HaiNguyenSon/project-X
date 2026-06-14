using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;
using PersonChatBot.Models;

namespace PersonChatBot.Ingestion;

/// <summary>
/// Splits extracted pages into overlapping chunks. Token budgets are approximated
/// from characters (~4 chars/token) — good enough for sizing without a tokenizer.
/// Chunks never span pages, so each chunk keeps an accurate page number for citations.
/// </summary>
public sealed partial class Chunker
{
    private const int CharsPerToken = 4;
    private readonly int _chunkChars;
    private readonly int _overlapChars;

    public Chunker(IOptions<RagOptions> options)
    {
        var o = options.Value;
        _chunkChars = Math.Max(200, o.ChunkSizeTokens * CharsPerToken);
        _overlapChars = Math.Clamp(o.ChunkOverlapTokens * CharsPerToken, 0, _chunkChars / 2);
    }

    /// <summary>Produces chunks with a file-global, sequential ChunkIndex.</summary>
    public IReadOnlyList<(int Page, int ChunkIndex, string Text)> Chunk(IReadOnlyList<ExtractedPage> pages)
    {
        var result = new List<(int, int, string)>();
        var chunkIndex = 0;

        foreach (var page in pages)
        {
            var text = NormalizeWhitespace(page.Text);
            if (text.Length == 0) continue;

            var start = 0;
            while (start < text.Length)
            {
                var end = Math.Min(start + _chunkChars, text.Length);

                // Prefer to break on whitespace near the window end (avoid splitting words).
                if (end < text.Length)
                {
                    var breakAt = text.LastIndexOf(' ', end - 1, Math.Min(end - start, 200));
                    if (breakAt > start)
                        end = breakAt;
                }

                var chunk = text[start..end].Trim();
                if (chunk.Length > 0)
                    result.Add((page.PageNumber, chunkIndex++, chunk));

                if (end >= text.Length) break;
                start = Math.Max(end - _overlapChars, start + 1);
            }
        }

        return result;
    }

    private static string NormalizeWhitespace(string text)
        => WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
