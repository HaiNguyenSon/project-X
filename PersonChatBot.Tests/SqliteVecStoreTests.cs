using PersonChatBot.Models;
using PersonChatBot.Storage;

namespace PersonChatBot.Tests;

public class SqliteVecStoreTests
{
    private const int Dim = 4;

    private static ChunkRecord Chunk(int page, int index, string text, params float[] vec) =>
        new(page, index, text, vec);

    private static async Task WithStore(Func<SqliteVecStore, Task> body)
    {
        var store = TestSupport.CreateStore(Dim, out var dbPath);
        try
        {
            await store.InitializeAsync();
            await body(store);
        }
        finally
        {
            await store.DisposeAsync();
            try { File.Delete(dbPath); } catch { /* pooled handle; temp file */ }
        }
    }

    [Fact]
    public Task New_store_is_empty() => WithStore(async store =>
    {
        var stats = await store.GetStatsAsync();
        Assert.Equal(0, stats.FileCount);
        Assert.Equal(0, stats.ChunkCount);
        Assert.Null(stats.LastIndexedAt);
        Assert.Null(await store.GetFileHashAsync(@"C:\nope.txt"));
    });

    [Fact]
    public Task Upsert_then_search_ranks_nearest_chunk_first() => WithStore(async store =>
    {
        await store.UpsertFileAsync(@"C:\a.pdf", "a.pdf", "HASH", new List<ChunkRecord>
        {
            Chunk(1, 0, "about cats", 1, 0, 0, 0),
            Chunk(2, 1, "about dogs", 0, 1, 0, 0),
            Chunk(3, 2, "about fish", 0, 0, 1, 0),
        });

        var hits = await store.SearchAsync(TestSupport.Vec(0.9f, 0.1f, 0f, 0f), topK: 3);

        Assert.Equal(3, hits.Count);
        Assert.Equal("about cats", hits[0].Text);       // nearest to [1,0,0,0]
        Assert.Equal(1, hits[0].Page);                   // page metadata round-trips
        Assert.True(hits[0].Score > hits[1].Score, "scores should be descending");
    });

    [Fact]
    public Task TopK_limits_the_number_of_results() => WithStore(async store =>
    {
        await store.UpsertFileAsync(@"C:\a.pdf", "a.pdf", "HASH", new List<ChunkRecord>
        {
            Chunk(0, 0, "one", 1, 0, 0, 0),
            Chunk(0, 1, "two", 0, 1, 0, 0),
            Chunk(0, 2, "three", 0, 0, 1, 0),
        });

        var hits = await store.SearchAsync(TestSupport.Vec(1, 0, 0, 0), topK: 2);
        Assert.Equal(2, hits.Count);
    });

    [Fact]
    public Task GetFileHash_and_stats_reflect_stored_data() => WithStore(async store =>
    {
        await store.UpsertFileAsync(@"C:\a.pdf", "a.pdf", "HASH_A", new List<ChunkRecord>
        {
            Chunk(1, 0, "x", 1, 0, 0, 0),
            Chunk(1, 1, "y", 0, 1, 0, 0),
        });
        await store.UpsertFileAsync(@"C:\b.txt", "b.txt", "HASH_B", new List<ChunkRecord>
        {
            Chunk(0, 0, "z", 0, 0, 1, 0),
        });

        Assert.Equal("HASH_A", await store.GetFileHashAsync(@"C:\a.pdf"));

        var stats = await store.GetStatsAsync();
        Assert.Equal(2, stats.FileCount);
        Assert.Equal(3, stats.ChunkCount);
        // Upserting files does not stamp "last indexed" — that is recorded deliberately
        // (only after a successful index), so a failed pass can't advance it.
        Assert.Null(stats.LastIndexedAt);

        var paths = await store.GetIndexedFilePathsAsync();
        Assert.Equal(2, paths.Count);
        Assert.Contains(@"C:\a.pdf", paths);
    });

    [Fact]
    public Task SetLastIndexedAt_is_recorded_and_overwritten() => WithStore(async store =>
    {
        Assert.Null((await store.GetStatsAsync()).LastIndexedAt);

        var first = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
        await store.SetLastIndexedAtAsync(first);
        Assert.Equal(first, (await store.GetStatsAsync()).LastIndexedAt);

        var second = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero);
        await store.SetLastIndexedAtAsync(second);                  // upsert, not a duplicate row
        Assert.Equal(second, (await store.GetStatsAsync()).LastIndexedAt);
    });

    [Fact]
    public Task Reindexing_a_file_replaces_its_chunks_without_leaving_stale_rows() => WithStore(async store =>
    {
        await store.UpsertFileAsync(@"C:\a.pdf", "a.pdf", "HASH_1", new List<ChunkRecord>
        {
            Chunk(1, 0, "old one", 1, 0, 0, 0),
            Chunk(1, 1, "old two", 0, 1, 0, 0),
        });

        // Re-index the same file with fewer chunks and a new hash.
        await store.UpsertFileAsync(@"C:\a.pdf", "a.pdf", "HASH_2", new List<ChunkRecord>
        {
            Chunk(1, 0, "new only", 1, 0, 0, 0),
        });

        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.FileCount);
        Assert.Equal(1, stats.ChunkCount);                          // not 3 — old rows gone
        Assert.Equal("HASH_2", await store.GetFileHashAsync(@"C:\a.pdf"));

        var hits = await store.SearchAsync(TestSupport.Vec(1, 0, 0, 0), topK: 5);
        Assert.Equal("new only", Assert.Single(hits).Text);
    });

    [Fact]
    public Task DeleteFile_removes_the_file_and_its_chunks() => WithStore(async store =>
    {
        await store.UpsertFileAsync(@"C:\a.pdf", "a.pdf", "HASH_A", new List<ChunkRecord> { Chunk(1, 0, "x", 1, 0, 0, 0) });
        await store.UpsertFileAsync(@"C:\b.txt", "b.txt", "HASH_B", new List<ChunkRecord> { Chunk(0, 0, "y", 0, 1, 0, 0) });

        await store.DeleteFileAsync(@"C:\b.txt");

        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.FileCount);
        Assert.Equal(1, stats.ChunkCount);
        Assert.Null(await store.GetFileHashAsync(@"C:\b.txt"));
    });
}
