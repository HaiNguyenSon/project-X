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

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IOptions<RagOptions> options)
    {
        _generator = generator;
        _documentPrefix = options.Value.EmbeddingDocumentPrefix;
        _queryPrefix = options.Value.EmbeddingQueryPrefix;
    }

    /// <summary>Embed a search query (uses the query task prefix).</summary>
    public async Task<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken ct = default)
        => await _generator.GenerateVectorAsync(_queryPrefix + text, cancellationToken: ct);

    /// <summary>Embed document chunks (uses the document task prefix).</summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return [];

        var prefixed = texts.Select(t => _documentPrefix + t).ToList();
        var embeddings = await _generator.GenerateAsync(prefixed, cancellationToken: ct);
        return embeddings.Select(e => e.Vector).ToList();
    }
}
