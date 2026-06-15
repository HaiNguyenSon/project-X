using System.Net.Sockets;
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
    private readonly string _endpoint;
    private readonly string _model;

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IOptions<RagOptions> options)
    {
        _generator = generator;
        _documentPrefix = options.Value.EmbeddingDocumentPrefix;
        _queryPrefix = options.Value.EmbeddingQueryPrefix;
        _batchSize = Math.Max(1, options.Value.EmbeddingBatchSize);
        _endpoint = options.Value.OllamaEndpoint;
        _model = options.Value.EmbeddingModel;
    }

    /// <summary>Embed a search query (uses the query task prefix).</summary>
    public async Task<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        try
        {
            return await _generator.GenerateVectorAsync(_queryPrefix + text, cancellationToken: ct);
        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {
            throw Unavailable(ex);
        }
    }

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
            try
            {
                var embeddings = await _generator.GenerateAsync(batch, cancellationToken: ct);
                results.AddRange(embeddings.Select(e => e.Vector));
            }
            catch (Exception ex) when (IsConnectivityFailure(ex))
            {
                throw Unavailable(ex);
            }
        }
        return results;
    }

    /// <summary>
    /// True when the exception indicates the embedding host couldn't be reached, rather than
    /// a bad request — i.e. Ollama isn't running. HttpRequestException (connection refused /
    /// DNS) and SocketException are the signals; we walk the inner-exception chain to find them.
    /// </summary>
    private static bool IsConnectivityFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or SocketException)
                return true;
        }
        return false;
    }

    private EmbeddingServiceUnavailableException Unavailable(Exception inner) =>
        new($"Couldn't reach the embedding model '{_model}' at {_endpoint}. " +
            $"Make sure Ollama is running (run 'ollama serve') and the model is pulled " +
            $"(run 'ollama pull {_model}').",
            inner);
}
