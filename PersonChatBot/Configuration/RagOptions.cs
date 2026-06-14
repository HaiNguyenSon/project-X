namespace PersonChatBot.Configuration;

/// <summary>
/// All tunable settings for the local RAG pipeline, bound from the "Rag" section
/// of appsettings.json. Everything here stays on the machine.
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Folder scanned (recursively) for documents to index.</summary>
    public string DocumentsFolder { get; set; } = "Documents";

    /// <summary>Base URL of the local Ollama server.</summary>
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>Chat/completion model served by Ollama. Swap to a 7B for A/B quality.</summary>
    public string ChatModel { get; set; } = "llama3.2:3b";

    /// <summary>Embedding model served by Ollama.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Dimension of the embedding vectors (nomic-embed-text = 768).</summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>
    /// Prefix added to document chunks before embedding. nomic-embed-text is trained
    /// with task prefixes and retrieves noticeably better with them. Set to "" for
    /// embedding models that don't use prefixes.
    /// </summary>
    public string EmbeddingDocumentPrefix { get; set; } = "search_document: ";

    /// <summary>Prefix added to the user's query before embedding (see above).</summary>
    public string EmbeddingQueryPrefix { get; set; } = "search_query: ";

    /// <summary>
    /// How many chunks to embed per Ollama request. Avoids sending one huge request
    /// for a large document (slow, memory-heavy, timeout-prone).
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 16;

    /// <summary>Approximate target chunk size in tokens (estimated from characters).</summary>
    public int ChunkSizeTokens { get; set; } = 750;

    /// <summary>Token overlap between consecutive chunks.</summary>
    public int ChunkOverlapTokens { get; set; } = 100;

    /// <summary>Number of nearest chunks retrieved per query.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Minimum cosine similarity (0..1) a chunk must reach to be used as context.
    /// Filters out weak matches so the model isn't grounded on barely-related text.
    /// Tune per corpus: lower it if good answers are being missed, raise it if the
    /// model cites irrelevant sources. 0 disables filtering.
    /// </summary>
    public double MinRelevanceScore { get; set; } = 0.35;

    /// <summary>Sampling temperature for the chat model. Low keeps answers grounded.</summary>
    public float Temperature { get; set; } = 0.2f;

    /// <summary>
    /// Maximum number of prior conversation turns sent back to the model. Keeps the
    /// system prompt + retrieved context + history within the model's context window.
    /// 0 means no limit.
    /// </summary>
    public int MaxHistoryTurns { get; set; } = 6;

    /// <summary>Path to the single-file SQLite vector database.</summary>
    public string DatabasePath { get; set; } = "rag.db";

    /// <summary>File extensions that will be ingested (lower-case, leading dot).</summary>
    public string[] SupportedExtensions { get; set; } = [".pdf", ".docx", ".txt", ".md"];

    /// <summary>
    /// Quiet period (ms) a file must be idle after its last change event before it is
    /// (re)indexed. File systems fire several events per save; this debounces them.
    /// </summary>
    public int WatchDebounceMs { get; set; } = 750;

    /// <summary>
    /// How many times to retry indexing a file that fails transiently (e.g. it is
    /// still being copied and is locked) before giving up. Each retry waits one
    /// debounce period, so this also bounds total retry time.
    /// </summary>
    public int WatchMaxRetries { get; set; } = 10;
}
