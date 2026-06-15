using Microsoft.Extensions.Logging.Abstractions;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;
using PersonChatBot.Ingestion;
using PersonChatBot.Models;
using PersonChatBot.Storage;

namespace PersonChatBot.Tests;

public class IndexingServiceTests
{
    private const int Dim = 8;

    /// <summary>Build the real ingestion graph over a temp folder + temp db with a fake embedder.</summary>
    private static async Task WithIndexing(
        Func<string, IndexingService, IVectorStore, Task> body,
        Action<RagOptions>? configure = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pcbdocs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(Path.GetTempPath(), $"pcbidx_{Guid.NewGuid():N}.db");

        var ragOptions = new RagOptions
        {
            DocumentsFolder = folder,
            DatabasePath = dbPath,
            EmbeddingDimensions = Dim,
        };
        configure?.Invoke(ragOptions);
        var options = TestSupport.Options(ragOptions);

        var extraction = new TextExtractionService(new ITextExtractor[]
        {
            new PlainTextExtractor(), new DocxTextExtractor(), new PdfTextExtractor(),
        });
        var chunker = new Chunker(options);
        var embeddings = TestSupport.Embedder(Dim);
        var store = new SqliteVecStore(options, NullLogger<SqliteVecStore>.Instance);
        var indexing = new IndexingService(
            options, extraction, chunker, embeddings, store, NullLogger<IndexingService>.Instance);

        try
        {
            await store.InitializeAsync();
            await body(folder, indexing, store);
        }
        finally
        {
            await store.DisposeAsync();
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public Task Indexing_a_new_file_stores_it() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "note.txt");
        await File.WriteAllTextAsync(path, "The sky is blue and the grass is green.");

        var outcome = await indexing.IndexFileAsync(path);

        Assert.Equal(IndexOutcome.Indexed, outcome);
        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.FileCount);
        Assert.True(stats.ChunkCount >= 1);
    });

    [Fact]
    public Task Unchanged_file_is_skipped_on_reindex() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "note.txt");
        await File.WriteAllTextAsync(path, "Stable content.");

        Assert.Equal(IndexOutcome.Indexed, await indexing.IndexFileAsync(path));    // first time
        Assert.Equal(IndexOutcome.Unchanged, await indexing.IndexFileAsync(path));  // hash match
    });

    [Fact]
    public Task Changed_file_is_reindexed() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "note.txt");
        await File.WriteAllTextAsync(path, "Version one.");
        Assert.Equal(IndexOutcome.Indexed, await indexing.IndexFileAsync(path));

        await File.WriteAllTextAsync(path, "Version two, now different.");
        Assert.Equal(IndexOutcome.Indexed, await indexing.IndexFileAsync(path));   // hash changed -> reindexed

        Assert.Equal(1, (await store.GetStatsAsync()).FileCount); // still one file, not duplicated
    });

    [Fact]
    public Task Unsupported_files_are_ignored() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "image.png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        Assert.Equal(IndexOutcome.Unsupported, await indexing.IndexFileAsync(path));
        Assert.Equal(0, (await store.GetStatsAsync()).FileCount);
    });

    [Fact]
    public Task File_with_no_extractable_text_reports_NoExtractableText() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "blank.txt");
        await File.WriteAllTextAsync(path, "   \r\n\t  "); // whitespace only -> no chunks

        Assert.Equal(IndexOutcome.NoExtractableText, await indexing.IndexFileAsync(path));
        Assert.Equal(0, (await store.GetStatsAsync()).FileCount);
    });

    [Fact]
    public Task ReindexAll_reports_files_with_no_extractable_text() => WithIndexing(async (folder, indexing, store) =>
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "good.txt"), "real content here");
        await File.WriteAllTextAsync(Path.Combine(folder, "blank.md"), "   ");

        var report = await indexing.ReindexAllAsync();

        Assert.Equal(1, report.FilesIndexed);
        Assert.Equal("blank.md", Assert.Single(report.NoTextFiles));
    });

    [Fact]
    public Task File_larger_than_the_size_limit_is_skipped() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "huge.txt");
        await File.WriteAllTextAsync(path, new string('x', 2 * 1024 * 1024)); // 2 MB, limit is 1 MB

        Assert.Equal(IndexOutcome.TooLarge, await indexing.IndexFileAsync(path));
        Assert.Equal(0, (await store.GetStatsAsync()).FileCount);
    }, configure: o => o.MaxFileSizeMb = 1);

    [Fact]
    public Task New_files_beyond_the_document_limit_are_skipped() => WithIndexing(async (folder, indexing, store) =>
    {
        var a = Path.Combine(folder, "a.txt");
        var b = Path.Combine(folder, "b.txt");
        var c = Path.Combine(folder, "c.txt");
        await File.WriteAllTextAsync(a, "first document");
        await File.WriteAllTextAsync(b, "second document");
        await File.WriteAllTextAsync(c, "third document");

        Assert.Equal(IndexOutcome.Indexed, await indexing.IndexFileAsync(a));
        Assert.Equal(IndexOutcome.Indexed, await indexing.IndexFileAsync(b));
        Assert.Equal(IndexOutcome.LimitReached, await indexing.IndexFileAsync(c)); // at capacity
        Assert.Equal(2, (await store.GetStatsAsync()).FileCount);

        // An already-indexed file can still be re-indexed even at the limit.
        await File.WriteAllTextAsync(a, "first document, edited");
        Assert.Equal(IndexOutcome.Indexed, await indexing.IndexFileAsync(a));
        Assert.Equal(2, (await store.GetStatsAsync()).FileCount);
    }, configure: o => o.MaxIndexedFiles = 2);

    [Fact]
    public Task ReindexAll_indexes_supported_files_and_ignores_others() => WithIndexing(async (folder, indexing, store) =>
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "alpha content");
        await File.WriteAllTextAsync(Path.Combine(folder, "b.md"), "beta content");
        await File.WriteAllBytesAsync(Path.Combine(folder, "c.png"), new byte[] { 9 });

        var report = await indexing.ReindexAllAsync();

        Assert.Equal(2, report.FilesIndexed);
        Assert.Equal(2, (await store.GetStatsAsync()).FileCount);
    });

    [Fact]
    public Task ReindexAll_prunes_files_deleted_from_disk() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "temp.txt");
        await File.WriteAllTextAsync(path, "to be deleted");
        await indexing.ReindexAllAsync();
        Assert.Equal(1, (await store.GetStatsAsync()).FileCount);

        File.Delete(path);
        var report = await indexing.ReindexAllAsync();

        Assert.Equal(1, report.FilesRemoved);
        Assert.Equal(0, (await store.GetStatsAsync()).FileCount);
    });
}
