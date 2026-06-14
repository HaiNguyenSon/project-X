using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;
using PersonChatBot.Models;
using PersonChatBot.Storage;

namespace PersonChatBot.Chat;

/// <summary>Embeds a query and returns the most relevant chunks above the score threshold.</summary>
public sealed class RetrievalService
{
    private readonly EmbeddingService _embeddings;
    private readonly IVectorStore _store;
    private readonly RagOptions _options;

    public RetrievalService(EmbeddingService embeddings, IVectorStore store, IOptions<RagOptions> options)
    {
        _embeddings = embeddings;
        _store = store;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<SearchHit>> RetrieveAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var queryVector = await _embeddings.EmbedQueryAsync(query, ct);
        var hits = await _store.SearchAsync(queryVector, _options.TopK, ct);

        return _options.MinRelevanceScore > 0
            ? hits.Where(h => h.Score >= _options.MinRelevanceScore).ToList()
            : hits;
    }
}
