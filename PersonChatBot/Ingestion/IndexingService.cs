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
    /// Index a single file. The returned <see cref="IndexOutcome"/> says what happened
    /// (indexed, unchanged, no extractable text, or unsupported).
    /// </summary>
    public async Task<IndexOutcome> IndexFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!_extraction.IsSupported(filePath) || !File.Exists(filePath))
            return IndexOutcome.Unsupported;

        // Reject oversized files before reading them, so a huge file can't exhaust memory.
        var sizeBytes = new FileInfo(filePath).Length;
        if (sizeBytes > _options.MaxFileSizeBytes)
        {
            await _store.DeleteFileAsync(filePath, ct); // drop stale chunks if it grew past the limit
            _logger.LogWarning(
                "Skipping {File}: {SizeMb:F1} MB exceeds the {LimitMb} MB limit.",
                filePath, sizeBytes / 1024d / 1024d, _options.MaxFileSizeMb);
            return IndexOutcome.TooLarge;
        }

        var hash = await ComputeFileHashAsync(filePath, ct);
        var existing = await _store.GetFileHashAsync(filePath, ct);
        if (existing == hash)
        {
            _logger.LogDebug("Skipping unchanged file {File}.", filePath);
            return IndexOutcome.Unchanged;
        }

        // Enforce the document cap for NEW files (already-indexed files may still update).
        if (existing is null && _options.MaxIndexedFiles > 0)
        {
            var stats = await _store.GetStatsAsync(ct);
            if (stats.FileCount >= _options.MaxIndexedFiles)
            {
                _logger.LogWarning(
                    "Skipping {File}: index already holds the maximum of {Limit} documents.",
                    filePath, _options.MaxIndexedFiles);
                return IndexOutcome.LimitReached;
            }
        }

        var pages = await _extraction.ExtractAsync(filePath, ct);
        var chunks = _chunker.Chunk(pages);
        if (chunks.Count == 0)
        {
            // Nothing extractable (e.g. a scanned/image-only PDF) — clear any stale chunks.
            await _store.DeleteFileAsync(filePath, ct);
            _logger.LogWarning("No extractable text in {File} (scanned/image PDF?); not indexed.", filePath);
            return IndexOutcome.NoExtractableText;
        }

        var vectors = await _embeddings.EmbedDocumentsAsync(chunks.Select(c => c.Text).ToList(), ct);
        if (vectors.Count != chunks.Count)
            throw new InvalidOperationException(
                $"Embedding count ({vectors.Count}) does not match chunk count ({chunks.Count}).");

        var records = chunks
            .Select((c, i) => new ChunkRecord(c.Page, c.ChunkIndex, c.Text, vectors[i]))
            .ToList();

        await _store.UpsertFileAsync(filePath, Path.GetFileName(filePath), hash, records, ct);
        _logger.LogInformation("Indexed {File}: {Chunks} chunks.", filePath, records.Count);
        return IndexOutcome.Indexed;
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

        int indexed = 0, skipped = 0, removed = 0, overLimit = 0;
        var noText = new List<string>();
        var oversized = new List<string>();
        var errors = new List<string>();

        foreach (var file in onDisk)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (await IndexFileAsync(file, ct))
                {
                    case IndexOutcome.Indexed: indexed++; break;
                    case IndexOutcome.Unchanged: skipped++; break;
                    case IndexOutcome.NoExtractableText: noText.Add(Path.GetFileName(file)); break;
                    case IndexOutcome.TooLarge: oversized.Add(Path.GetFileName(file)); break;
                    case IndexOutcome.LimitReached: overLimit++; break;
                }
            }
            catch (EmbeddingServiceUnavailableException)
            {
                // The embedder is down, so every remaining file would fail identically.
                // Abort the whole pass with the single actionable message rather than
                // logging the same connectivity error once per file.
                throw;
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

        // Only stamp "last indexed" when the pass actually indexed something and hit no errors.
        // An aborted pass throws before here; an all-unchanged pass leaves the stamp as-is.
        if (errors.Count == 0 && indexed > 0)
            await _store.SetLastIndexedAtAsync(DateTimeOffset.UtcNow, ct);

        var stats = await _store.GetStatsAsync(ct);

        _logger.LogInformation(
            "Reindex complete: {Indexed} indexed, {Skipped} skipped, {Removed} removed, " +
            "{NoText} with no text, {Oversized} too large, {OverLimit} over the file limit.",
            indexed, skipped, removed, noText.Count, oversized.Count, overLimit);
        return new IndexReport(indexed, skipped, removed, stats.ChunkCount, noText, oversized, overLimit, errors);
    }

    /// <summary>
    /// Index a single file (used by the live folder watcher) and, if it was actually indexed,
    /// record the successful-index time. Unlike a reindex pass, one settled file is a complete
    /// unit of work, so its success stands on its own.
    /// </summary>
    public async Task<IndexOutcome> IndexWatchedFileAsync(string filePath, CancellationToken ct = default)
    {
        var outcome = await IndexFileAsync(filePath, ct);
        if (outcome == IndexOutcome.Indexed)
            await _store.SetLastIndexedAtAsync(DateTimeOffset.UtcNow, ct);
        return outcome;
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }
}
