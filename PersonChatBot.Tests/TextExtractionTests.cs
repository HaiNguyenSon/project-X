using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PersonChatBot.Ingestion;

namespace PersonChatBot.Tests;

public class TextExtractionTests
{
    private static TextExtractionService MakeService() =>
        new(new ITextExtractor[] { new PlainTextExtractor(), new DocxTextExtractor(), new PdfTextExtractor() });

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("a.md", true)]
    [InlineData("a.pdf", true)]
    [InlineData("a.docx", true)]
    [InlineData("a.png", false)]
    [InlineData("a.zip", false)]
    [InlineData("a.TXT", true)] // case-insensitive
    public void IsSupported_recognizes_expected_extensions(string fileName, bool expected)
    {
        Assert.Equal(expected, MakeService().IsSupported(fileName));
    }

    [Fact]
    public async Task PlainText_file_is_read_as_a_single_page()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcb_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "Hello from a text file.");
        try
        {
            var pages = await MakeService().ExtractAsync(path);

            var page = Assert.Single(pages);
            Assert.Equal(0, page.PageNumber);            // txt has no page concept
            Assert.Contains("Hello from a text file.", page.Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Docx_file_text_is_extracted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcb_{Guid.NewGuid():N}.docx");
        CreateDocx(path, "First paragraph.", "Second paragraph.");
        try
        {
            var pages = await MakeService().ExtractAsync(path);

            var page = Assert.Single(pages);
            Assert.Contains("First paragraph.", page.Text);
            Assert.Contains("Second paragraph.", page.Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unsupported_extension_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcb_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => MakeService().ExtractAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static void CreateDocx(string path, params string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(
            paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
        main.Document.Save();
    }
}
