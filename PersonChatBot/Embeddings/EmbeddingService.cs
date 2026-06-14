using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;

namespace PersonChatBot.Embeddings;

/// <summary>
/// Thin wrapper over the local Ollama embedding generator. Keeps the rest of the
/// app free of Microsoft.Extensions.AI types, applies the model's task prefixes,
/// and gives us a single place to batch.
/// </summary>
public sealed class EmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly string _documentPrefix;
    private readonly string _queryPrefix;
    private readonly int _batchSize;

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IOptions<RagOptions> options)
    {
        _generator = generator;
        _documentPrefix = options.Value.EmbeddingDocumentPrefix;
        _queryPrefix = options.Value.EmbeddingQueryPrefix;
        _batchSize = Math.Max(1, options.Value.EmbeddingBatchSize);
    }

    /// <summary>Embed a search query (uses the query task prefix).</summary>
    public async Task<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken ct = default)
        => await _generator.GenerateVectorAsync(_queryPrefix + text, cancellationToken: ct);

    /// <summary>Embed document chunks (uses the document task prefix), in batches.</summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return [];

        var results = new List<ReadOnlyMemory<float>>(texts.Count);
        for (var start = 0; start < texts.Count; start += _batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = texts.Skip(start).Take(_batchSize).Select(t => _documentPrefix + t).ToList();
            var embeddings = await _generator.GenerateAsync(batch, cancellationToken: ct);
            results.AddRange(embeddings.Select(e => e.Vector));
        }
        return results;
    }
}
