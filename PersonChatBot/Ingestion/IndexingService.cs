using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;
using PersonChatBot.Models;
using PersonChatBot.Storage;

namespace PersonChatBot.Ingestion;

/// <summary>
/// Orchestrates the ingestion pipeline: extract → chunk → embed → upsert.
/// Idempotent — files whose content hash is unchanged are skipped.
/// </summary>
public sealed class IndexingService
{
    private readonly RagOptions _options;
    private readonly TextExtractionService _extraction;
    private readonly Chunker _chunker;
    private readonly EmbeddingService _embeddings;
    private readonly IVectorStore _store;
    private readonly ILogger<IndexingService> _logger;

    public IndexingService(
        IOptions<RagOptions> options,
        TextExtractionService extraction,
        Chunker chunker,
        EmbeddingService embeddings,
        IVectorStore store,
        ILogger<IndexingService> logger)
    {
        _options = options.Value;
        _extraction = extraction;
        _chunker = chunker;
        _embeddings = embeddings;
        _store = store;
        _logger = logger;
    }

    public string DocumentsFolder => Path.GetFullPath(_options.DocumentsFolder);

    /// <summary>
    /// Index a single file. Returns true if it was (re)indexed, false if skipped
    /// (unchanged, unsupported, or empty).
    /// </summary>
    public async Task<bool> IndexFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!_extraction.IsSupported(filePath) || !File.Exists(filePath))
            return false;

        var hash = await ComputeFileHashAsync(filePath, ct);
        var existing = await _store.GetFileHashAsync(filePath, ct);
        if (existing == hash)
        {
            _logger.LogDebug("Skipping unchanged file {File}.", filePath);
            return false;
        }

        var pages = await _extraction.ExtractAsync(filePath, ct);
        var chunks = _chunker.Chunk(pages);
        if (chunks.Count == 0)
        {
            // Nothing extractable — make sure any stale chunks are cleared.
            await _store.DeleteFileAsync(filePath, ct);
            _logger.LogInformation("No extractable text in {File}; cleared from index.", filePath);
            return false;
        }

        var vectors = await _embeddings.EmbedBatchAsync(chunks.Select(c => c.Text).ToList(), ct);
        if (vectors.Count != chunks.Count)
            throw new InvalidOperationException(
                $"Embedding count ({vectors.Count}) does not match chunk count ({chunks.Count}).");

        var records = chunks
            .Select((c, i) => new ChunkRecord(c.Page, c.ChunkIndex, c.Text, vectors[i]))
            .ToList();

        await _store.UpsertFileAsync(filePath, Path.GetFileName(filePath), hash, records, ct);
        _logger.LogInformation("Indexed {File}: {Chunks} chunks.", filePath, records.Count);
        return true;
    }

    public Task RemoveFileAsync(string filePath, CancellationToken ct = default)
        => _store.DeleteFileAsync(filePath, ct);

    /// <summary>
    /// Full pass over the documents folder: index new/changed files, prune files
    /// that no longer exist on disk.
    /// </summary>
    public async Task<IndexReport> ReindexAllAsync(CancellationToken ct = default)
    {
        await _store.InitializeAsync(ct);

        var folder = DocumentsFolder;
        Directory.CreateDirectory(folder);

        var onDisk = Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(_extraction.IsSupported)
            .ToList();

        int indexed = 0, skipped = 0, removed = 0, chunksWritten = 0;
        var errors = new List<string>();

        foreach (var file in onDisk)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var statsBefore = await _store.GetFileHashAsync(file, ct);
                if (await IndexFileAsync(file, ct))
                {
                    indexed++;
                    // chunk count is tracked in the store; recomputing here is cheap enough to skip.
                }
                else if (statsBefore is not null)
                {
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to index {File}.", file);
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Prune files that were indexed before but have since been deleted.
        var onDiskSet = onDisk.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var indexedPath in await _store.GetIndexedFilePathsAsync(ct))
        {
            if (!onDiskSet.Contains(indexedPath))
            {
                await _store.DeleteFileAsync(indexedPath, ct);
                removed++;
                _logger.LogInformation("Pruned deleted file {File} from index.", indexedPath);
            }
        }

        var stats = await _store.GetStatsAsync(ct);
        chunksWritten = stats.ChunkCount;

        _logger.LogInformation(
            "Reindex complete: {Indexed} indexed, {Skipped} skipped, {Removed} removed.",
            indexed, skipped, removed);
        return new IndexReport(indexed, skipped, removed, chunksWritten, errors);
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }
}
