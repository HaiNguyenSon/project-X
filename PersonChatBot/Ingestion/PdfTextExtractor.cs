using PersonChatBot.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PersonChatBot.Ingestion;

/// <summary>Extracts text per page from PDFs using PdfPig, preserving page numbers.</summary>
public sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".pdf";

    public Task<IReadOnlyList<ExtractedPage>> ExtractAsync(string filePath, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<ExtractedPage>>(() =>
        {
            var pages = new List<ExtractedPage>();
            using var document = PdfDocument.Open(filePath);
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                // ContentOrderTextExtractor gives more natural reading order than page.Text.
                var text = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(text))
                    pages.Add(new ExtractedPage(page.Number, text));
            }
            return pages;
        }, ct);
}
