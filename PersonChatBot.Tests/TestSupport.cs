using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;
using PersonChatBot.Storage;

namespace PersonChatBot.Tests;

/// <summary>
/// Deterministic stand-in for the Ollama embedding generator so tests run without
/// any external service. Produces a fixed-dimension vector from the text's contents,
/// so identical text always yields the identical vector.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly int _dimensions;

    public FakeEmbeddingGenerator(int dimensions) => _dimensions = dimensions;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(v => new Embedding<float>(Vectorize(v))).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private float[] Vectorize(string text)
    {
        var vector = new float[_dimensions];
        // Spread character codes across the dimensions — deterministic and stable.
        for (var i = 0; i < text.Length; i++)
            vector[i % _dimensions] += (text[i] % 32) + 1;
        return vector;
    }
}

internal static class TestSupport
{
    public static IOptions<RagOptions> Options(RagOptions options) =>
        Microsoft.Extensions.Options.Options.Create(options);

    /// <summary>A vector store on a throwaway temp database file.</summary>
    public static SqliteVecStore CreateStore(int dimensions, out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"pcbtest_{Guid.NewGuid():N}.db");
        var options = new RagOptions { DatabasePath = dbPath, EmbeddingDimensions = dimensions };
        return new SqliteVecStore(Options(options), NullLogger<SqliteVecStore>.Instance);
    }

    public static ReadOnlyMemory<float> Vec(params float[] values) => values;

    /// <summary>An EmbeddingService backed by the deterministic fake generator.</summary>
    public static EmbeddingService Embedder(int dimensions) =>
        new(new FakeEmbeddingGenerator(dimensions), Options(new RagOptions { EmbeddingDimensions = dimensions }));
}
