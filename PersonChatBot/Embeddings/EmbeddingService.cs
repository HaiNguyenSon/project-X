using Microsoft.Extensions.AI;

namespace PersonChatBot.Embeddings;

/// <summary>
/// Thin wrapper over the local Ollama embedding generator. Keeps the rest of the
/// app free of Microsoft.Extensions.AI types and gives us a single place to batch.
/// </summary>
public sealed class EmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
        => _generator = generator;

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default)
        => await _generator.GenerateVectorAsync(text, cancellationToken: ct);

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return [];

        var embeddings = await _generator.GenerateAsync(texts, cancellationToken: ct);
        return embeddings.Select(e => e.Vector).ToList();
    }
}
