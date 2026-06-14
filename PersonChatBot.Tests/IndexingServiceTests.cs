using Microsoft.Extensions.Logging.Abstractions;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;
using PersonChatBot.Ingestion;
using PersonChatBot.Storage;

namespace PersonChatBot.Tests;

public class IndexingServiceTests
{
    private const int Dim = 8;

    /// <summary>Build the real ingestion graph over a temp folder + temp db with a fake embedder.</summary>
    private static async Task WithIndexing(Func<string, IndexingService, IVectorStore, Task> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pcbdocs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(Path.GetTempPath(), $"pcbidx_{Guid.NewGuid():N}.db");

        var options = TestSupport.Options(new RagOptions
        {
            DocumentsFolder = folder,
            DatabasePath = dbPath,
            EmbeddingDimensions = Dim,
        });

        var extraction = new TextExtractionService(new ITextExtractor[]
        {
            new PlainTextExtractor(), new DocxTextExtractor(), new PdfTextExtractor(),
        });
        var chunker = new Chunker(options);
        var embeddings = new EmbeddingService(new FakeEmbeddingGenerator(Dim));
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

        var indexed = await indexing.IndexFileAsync(path);

        Assert.True(indexed);
        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.FileCount);
        Assert.True(stats.ChunkCount >= 1);
    });

    [Fact]
    public Task Unchanged_file_is_skipped_on_reindex() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "note.txt");
        await File.WriteAllTextAsync(path, "Stable content.");

        Assert.True(await indexing.IndexFileAsync(path));   // first time: indexed
        Assert.False(await indexing.IndexFileAsync(path));  // unchanged: skipped (hash match)
    });

    [Fact]
    public Task Changed_file_is_reindexed() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "note.txt");
        await File.WriteAllTextAsync(path, "Version one.");
        Assert.True(await indexing.IndexFileAsync(path));

        await File.WriteAllTextAsync(path, "Version two, now different.");
        Assert.True(await indexing.IndexFileAsync(path));   // hash changed -> reindexed

        Assert.Equal(1, (await store.GetStatsAsync()).FileCount); // still one file, not duplicated
    });

    [Fact]
    public Task Unsupported_files_are_ignored() => WithIndexing(async (folder, indexing, store) =>
    {
        var path = Path.Combine(folder, "image.png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        Assert.False(await indexing.IndexFileAsync(path));
        Assert.Equal(0, (await store.GetStatsAsync()).FileCount);
    });

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
