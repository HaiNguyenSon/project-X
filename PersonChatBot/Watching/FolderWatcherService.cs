using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;
using PersonChatBot.Ingestion;

namespace PersonChatBot.Watching;

/// <summary>
/// Keeps the index in sync with the documents folder. On startup it runs a full
/// reindex (catching changes made while the app was off), then watches for file
/// system events. Events are debounced — editors fire several per save — and each
/// settled path is re-indexed or, if it no longer exists, removed.
/// </summary>
public sealed class FolderWatcherService : BackgroundService
{
    private readonly IndexingService _indexing;
    private readonly TextExtractionService _extraction;
    private readonly RagOptions _options;
    private readonly ILogger<FolderWatcherService> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _fullRescanRequested;

    public FolderWatcherService(
        IndexingService indexing,
        TextExtractionService extraction,
        IOptions<RagOptions> options,
        ILogger<FolderWatcherService> logger)
    {
        _indexing = indexing;
        _extraction = extraction;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string folder;
        try
        {
            folder = _indexing.DocumentsFolder;
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            // A misconfigured/unavailable documents folder must not crash the whole app
            // (an unhandled exception here would stop the host). Disable watching instead.
            _logger.LogError(ex,
                "Cannot access the documents folder; file indexing/watching is disabled. " +
                "Check the Rag:DocumentsFolder setting.");
            return;
        }

        // 1) One-time full index on startup.
        try
        {
            _logger.LogInformation("Startup reindex of {Folder}…", folder);
            var report = await _indexing.ReindexAllAsync(stoppingToken);
            _logger.LogInformation(
                "Startup reindex: {Indexed} indexed, {Skipped} unchanged, {Removed} removed.",
                report.FilesIndexed, report.FilesSkipped, report.FilesRemoved);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup reindex failed; continuing to watch for changes.");
        }

        // 2) Watch for changes.
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Startup indexing already ran; only live watching is lost. Don't crash the host.
            _logger.LogError(ex, "Failed to start the file watcher on {Folder}; live watching disabled.", folder);
            return;
        }

        _logger.LogInformation("Watching {Folder} for changes.", folder);
        using var _watcher = watcher;

        // 3) Debounce loop: process paths that have been quiet long enough.
        var debounce = TimeSpan.FromMilliseconds(_options.WatchDebounceMs);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            if (_fullRescanRequested)
            {
                _fullRescanRequested = false;
                _pending.Clear();
                try { await _indexing.ReindexAllAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Full rescan after watcher error failed."); }
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var ready = _pending
                .Where(kvp => now - kvp.Value >= debounce)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var path in ready)
            {
                if (!_pending.TryRemove(path, out _))
                    continue;

                try
                {
                    if (File.Exists(path))
                    {
                        if (_extraction.IsSupported(path))
                            await _indexing.IndexWatchedFileAsync(path, stoppingToken);
                    }
                    else
                    {
                        await _indexing.RemoveFileAsync(path, stoppingToken);
                    }
                    _attempts.TryRemove(path, out _); // succeeded — reset retry count
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (!IsTransient(ex))
                {
                    // A corrupt/unsupported-content file (bad PDF, broken .docx) fails the
                    // same way every time — don't retry it, just log once and move on.
                    _logger.LogError(ex, "Failed to process {Path}; not a transient error, not retrying.", path);
                    _attempts.TryRemove(path, out _);
                }
                catch (Exception ex)
                {
                    // Transient error (e.g. the file is still being copied and is locked).
                    // Retry a few times — re-enqueueing waits another debounce period.
                    var attempt = _attempts.AddOrUpdate(path, 1, (_, c) => c + 1);
                    if (attempt < _options.WatchMaxRetries)
                    {
                        _logger.LogWarning(ex,
                            "Transient error processing {Path} (attempt {Attempt}/{Max}); will retry.",
                            path, attempt, _options.WatchMaxRetries);
                        Enqueue(path);
                    }
                    else
                    {
                        _logger.LogError(ex,
                            "Giving up on {Path} after {Max} attempts.", path, _options.WatchMaxRetries);
                        _attempts.TryRemove(path, out _);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether an error processing a file is worth retrying. File-locking / access
    /// errors (the file is still being written) are transient; parsing/format errors
    /// from a corrupt document are not — retrying those just repeats the failure.
    /// </summary>
    internal static bool IsTransient(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private void OnChanged(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Old name disappears (will be removed), new name appears (will be indexed).
        Enqueue(e.OldFullPath);
        Enqueue(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or similar — we may have missed events, so rescan everything.
        _logger.LogWarning(e.GetException(), "File watcher error; scheduling a full rescan.");
        _fullRescanRequested = true;
    }

    private void Enqueue(string fullPath) => _pending[fullPath] = DateTimeOffset.UtcNow;
}
