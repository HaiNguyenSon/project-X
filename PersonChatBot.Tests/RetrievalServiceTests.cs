using PersonChatBot.Chat;
using PersonChatBot.Configuration;
using PersonChatBot.Models;
using PersonChatBot.Storage;

namespace PersonChatBot.Tests;

public class RetrievalServiceTests
{
    private static RetrievalService Make(IVectorStore store, double minScore) =>
        new(TestSupport.Embedder(4), store,
            TestSupport.Options(new RagOptions { EmbeddingDimensions = 4, TopK = 10, MinRelevanceScore = minScore }));

    [Fact]
    public async Task Hits_below_the_relevance_floor_are_filtered_out()
    {
        var store = new StubStore(
            new SearchHit(@"C:\a", "a", 1, 0, "strong", 0.80),
            new SearchHit(@"C:\b", "b", 1, 0, "weak", 0.20));
        var retrieval = Make(store, minScore: 0.35);

        var hits = await retrieval.RetrieveAsync("question");

        Assert.Equal("strong", Assert.Single(hits).Text);
    }

    [Fact]
    public async Task Zero_floor_keeps_everything()
    {
        var store = new StubStore(
            new SearchHit(@"C:\a", "a", 1, 0, "x", 0.80),
            new SearchHit(@"C:\b", "b", 1, 0, "y", 0.05));
        var retrieval = Make(store, minScore: 0.0);

        Assert.Equal(2, (await retrieval.RetrieveAsync("question")).Count);
    }

    [Fact]
    public async Task Blank_query_returns_no_hits()
    {
        var retrieval = Make(new StubStore(), minScore: 0.0);
        Assert.Empty(await retrieval.RetrieveAsync("   "));
    }

    /// <summary>Returns a fixed set of hits for any query, so the filtering logic can be tested in isolation.</summary>
    private sealed class StubStore(params SearchHit[] hits) : IVectorStore
    {
        public Task<IReadOnlyList<SearchHit>> SearchAsync(ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchHit>>(hits.Take(topK).ToList());

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetFileHashAsync(string filePath, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task UpsertFileAsync(string filePath, string fileName, string fileHash, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFileAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetIndexedFilePathsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IndexStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new IndexStats(0, 0, null));
    }
}
