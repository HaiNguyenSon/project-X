using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PersonChatBot.Models;

namespace PersonChatBot.Ingestion;

/// <summary>Extracts text from .docx files via OpenXml. Word has no fixed pages, so all
/// text is returned as a single page (PageNumber 0).</summary>
public sealed class DocxTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".docx";

    public Task<IReadOnlyList<ExtractedPage>> ExtractAsync(string filePath, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<ExtractedPage>>(() =>
        {
            using var document = WordprocessingDocument.Open(filePath, false);
            var body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
                return [];

            var lines = body.Descendants<Paragraph>()
                .Select(p => p.InnerText)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            var text = string.Join("\n", lines);

            return string.IsNullOrWhiteSpace(text)
                ? []
                : new List<ExtractedPage> { new(0, text) };
        }, ct);
}
