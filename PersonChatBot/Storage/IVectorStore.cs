using PersonChatBot.Models;

namespace PersonChatBot.Storage;

/// <summary>
/// Persistence + similarity search for document chunks. Implemented over
/// SQLite + sqlite-vec, but kept behind this interface so the backing store
/// can be swapped without touching ingestion, retrieval, or the UI.
/// </summary>
public interface IVectorStore
{
    /// <summary>Open the database, load sqlite-vec, and create the schema if needed.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>The content hash recorded for a file, or null if it isn't indexed.</summary>
    Task<string?> GetFileHashAsync(string filePath, CancellationToken ct = default);

    /// <summary>Replace all chunks for a file (delete-then-insert) and record its hash.</summary>
    Task UpsertFileAsync(
        string filePath,
        string fileName,
        string fileHash,
        IReadOnlyList<ChunkRecord> chunks,
        CancellationToken ct = default);

    /// <summary>Remove a file and all of its chunks.</summary>
    Task DeleteFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>All file paths currently indexed (used to prune deleted files).</summary>
    Task<IReadOnlyList<string>> GetIndexedFilePathsAsync(CancellationToken ct = default);

    /// <summary>Counts and last-index time for the status panel.</summary>
    Task<IndexStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Record the time of a successful index. Called deliberately (after a clean reindex
    /// pass or a successful single-file index), so a failed run never advances it.
    /// </summary>
    Task SetLastIndexedAtAsync(DateTimeOffset value, CancellationToken ct = default);

    /// <summary>Top-K nearest chunks to the query vector (cosine distance).</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default);
}
