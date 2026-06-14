using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;
using PersonChatBot.Models;

namespace PersonChatBot.Storage;

/// <summary>
/// Vector store backed by SQLite + the sqlite-vec extension, in a single file.
/// A long-lived connection is serialized with a semaphore — fine for a personal,
/// small-scale (&lt;100 docs) app and far simpler than per-op connection pooling
/// with repeated extension loading.
/// </summary>
public sealed class SqliteVecStore : IVectorStore, IAsyncDisposable
{
    private readonly RagOptions _options;
    private readonly ILogger<SqliteVecStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _initialized;

    public SqliteVecStore(IOptions<RagOptions> options, ILogger<SqliteVecStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var dbPath = Path.GetFullPath(_options.DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();

            LoadVecExtension(_connection);

            var dim = _options.EmbeddingDimensions;
            await ExecAsync(
                """
                CREATE TABLE IF NOT EXISTS files (
                    file_path   TEXT PRIMARY KEY,
                    file_name   TEXT NOT NULL,
                    file_hash   TEXT NOT NULL,
                    chunk_count INTEGER NOT NULL,
                    indexed_at  TEXT NOT NULL
                );
                """, ct);

            await ExecAsync(
                $"""
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks USING vec0(
                    embedding   float[{dim}] distance_metric=cosine,
                    file_path   TEXT,
                    +file_name  TEXT,
                    +page       INTEGER,
                    +chunk_index INTEGER,
                    +text       TEXT
                );
                """, ct);

            _initialized = true;
            _logger.LogInformation("Vector store initialized at {Path} (dim={Dim}).", dbPath, dim);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetFileHashAsync(string filePath, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT file_hash FROM files WHERE file_path = @p LIMIT 1;";
            cmd.Parameters.AddWithValue("@p", filePath);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertFileAsync(
        string filePath, string fileName, string fileHash,
        IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection!.BeginTransactionAsync(ct);

            await DeleteFileInternalAsync(filePath, (SqliteTransaction)tx, ct);

            using (var insertFile = _connection.CreateCommand())
            {
                insertFile.Transaction = (SqliteTransaction)tx;
                insertFile.CommandText =
                    """
                    INSERT INTO files (file_path, file_name, file_hash, chunk_count, indexed_at)
                    VALUES (@path, @name, @hash, @count, @at);
                    """;
                insertFile.Parameters.AddWithValue("@path", filePath);
                insertFile.Parameters.AddWithValue("@name", fileName);
                insertFile.Parameters.AddWithValue("@hash", fileHash);
                insertFile.Parameters.AddWithValue("@count", chunks.Count);
                insertFile.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
                await insertFile.ExecuteNonQueryAsync(ct);
            }

            using (var insertChunk = _connection.CreateCommand())
            {
                insertChunk.Transaction = (SqliteTransaction)tx;
                insertChunk.CommandText =
                    """
                    INSERT INTO chunks (embedding, file_path, file_name, page, chunk_index, text)
                    VALUES (@embedding, @path, @name, @page, @idx, @text);
                    """;
                var pEmbedding = insertChunk.Parameters.Add("@embedding", SqliteType.Blob);
                var pPath = insertChunk.Parameters.Add("@path", SqliteType.Text);
                var pName = insertChunk.Parameters.Add("@name", SqliteType.Text);
                var pPage = insertChunk.Parameters.Add("@page", SqliteType.Integer);
                var pIdx = insertChunk.Parameters.Add("@idx", SqliteType.Integer);
                var pText = insertChunk.Parameters.Add("@text", SqliteType.Text);

                foreach (var chunk in chunks)
                {
                    pEmbedding.Value = ToBlob(chunk.Embedding);
                    pPath.Value = filePath;
                    pName.Value = fileName;
                    pPage.Value = chunk.Page;
                    pIdx.Value = chunk.ChunkIndex;
                    pText.Value = chunk.Text;
                    await insertChunk.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteFileAsync(string filePath, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection!.BeginTransactionAsync(ct);
            await DeleteFileInternalAsync(filePath, (SqliteTransaction)tx, ct);
            await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetIndexedFilePathsAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var paths = new List<string>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT file_path FROM files;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                paths.Add(reader.GetString(0));
            return paths;
        }
        finally { _gate.Release(); }
    }

    public async Task<IndexStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*), COALESCE(SUM(chunk_count), 0), MAX(indexed_at) FROM files;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new IndexStats(0, 0, null);

            var fileCount = reader.GetInt32(0);
            var chunkCount = reader.GetInt32(1);
            DateTimeOffset? lastAt = reader.IsDBNull(2)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2));
            return new IndexStats(fileCount, chunkCount, lastAt);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var hits = new List<SearchHit>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText =
                """
                SELECT file_path, file_name, page, chunk_index, text, distance
                FROM chunks
                WHERE embedding MATCH @q AND k = @k
                ORDER BY distance;
                """;
            cmd.Parameters.Add("@q", SqliteType.Blob).Value = ToBlob(queryVector);
            cmd.Parameters.AddWithValue("@k", topK);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var distance = reader.GetDouble(5);
                hits.Add(new SearchHit(
                    FilePath: reader.GetString(0),
                    FileName: reader.GetString(1),
                    Page: reader.GetInt32(2),
                    ChunkIndex: reader.GetInt32(3),
                    Text: reader.GetString(4),
                    Score: 1.0 - distance)); // cosine distance -> similarity
            }
            return hits;
        }
        finally { _gate.Release(); }
    }

    // --- internals ---

    private async Task DeleteFileInternalAsync(string filePath, SqliteTransaction tx, CancellationToken ct)
    {
        // Collect rowids first: vec0 DELETE-by-metadata support varies by version,
        // but DELETE-by-rowid is always supported.
        var rowids = new List<long>();
        using (var select = _connection!.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT rowid FROM chunks WHERE file_path = @p;";
            select.Parameters.AddWithValue("@p", filePath);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rowids.Add(reader.GetInt64(0));
        }

        if (rowids.Count > 0)
        {
            using var del = _connection!.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM chunks WHERE rowid = @r;";
            var pr = del.Parameters.Add("@r", SqliteType.Integer);
            foreach (var id in rowids)
            {
                pr.Value = id;
                await del.ExecuteNonQueryAsync(ct);
            }
        }

        using var delFile = _connection!.CreateCommand();
        delFile.Transaction = tx;
        delFile.CommandText = "DELETE FROM files WHERE file_path = @p;";
        delFile.Parameters.AddWithValue("@p", filePath);
        await delFile.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeAsync(ct);
    }

    private void LoadVecExtension(SqliteConnection connection)
    {
        var path = ResolveVecExtensionPath();
        connection.LoadExtension(path);
        _logger.LogInformation("Loaded sqlite-vec extension from {Path}.", path);
    }

    private static string ResolveVecExtensionPath()
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vec0.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "vec0.dylib"
            : "vec0.so";

        var baseDir = AppContext.BaseDirectory;
        var rid = RuntimeInformation.RuntimeIdentifier;
        var candidates = new[]
        {
            Path.Combine(baseDir, "runtimes", rid, "native", fileName),
            Path.Combine(baseDir, fileName),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        // Fall back to any runtimes/*/native match (RID may not resolve exactly).
        var runtimesDir = Path.Combine(baseDir, "runtimes");
        if (Directory.Exists(runtimesDir))
        {
            var match = Directory.GetFiles(runtimesDir, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (match is not null)
                return match;
        }

        // Last resort: let the OS loader search (PATH / app dir).
        return Path.GetFileNameWithoutExtension(fileName); // "vec0"
    }

    private static byte[] ToBlob(ReadOnlyMemory<float> vector)
    {
        var span = vector.Span;
        var bytes = new byte[span.Length * sizeof(float)];
        MemoryMarshal.AsBytes(span).CopyTo(bytes);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _gate.Dispose();
    }
}
