namespace PersonChatBot.Embeddings;

/// <summary>
/// Thrown when the embedding model can't be reached (e.g. Ollama isn't running or the
/// model isn't pulled). Carries an actionable message instead of a raw socket error.
/// </summary>
public sealed class EmbeddingServiceUnavailableException : Exception
{
    public EmbeddingServiceUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
