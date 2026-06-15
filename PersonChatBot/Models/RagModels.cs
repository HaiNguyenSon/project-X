namespace PersonChatBot.Models;

/// <summary>A unit of text to embed and store, with its provenance within a file.</summary>
public sealed record ChunkRecord(
    int Page,
    int ChunkIndex,
    string Text,
    ReadOnlyMemory<float> Embedding);

/// <summary>A retrieved chunk plus its similarity score.</summary>
public sealed record SearchHit(
    string FilePath,
    string FileName,
    int Page,
    int ChunkIndex,
    string Text,
    double Score);

/// <summary>A page of extracted text. Page 0 means "no page concept" (txt/md/docx).</summary>
public readonly record struct ExtractedPage(int PageNumber, string Text);

/// <summary>Snapshot of what is currently indexed, for the status panel.</summary>
public sealed record IndexStats(int FileCount, int ChunkCount, DateTimeOffset? LastIndexedAt);

/// <summary>Who authored a conversation turn.</summary>
public enum ChatAuthor { User, Assistant }

/// <summary>One prior turn of conversation, passed back in for follow-up context.</summary>
public sealed record ChatTurn(ChatAuthor Author, string Content);

/// <summary>What happened when a single file was processed.</summary>
public enum IndexOutcome
{
    /// <summary>File was (re)indexed.</summary>
    Indexed,
    /// <summary>File content hash was unchanged; nothing to do.</summary>
    Unchanged,
    /// <summary>No extractable text (e.g. a scanned/image-only PDF); not indexed.</summary>
    NoExtractableText,
    /// <summary>File is larger than the configured size limit; not indexed.</summary>
    TooLarge,
    /// <summary>The indexed-file limit is reached and this is a new file; not indexed.</summary>
    LimitReached,
    /// <summary>File type isn't supported, or the file no longer exists.</summary>
    Unsupported,
}

/// <summary>Result of a (re)index pass over the documents folder.</summary>
public sealed record IndexReport(
    int FilesIndexed,
    int FilesSkipped,
    int FilesRemoved,
    int ChunksWritten,
    IReadOnlyList<string> NoTextFiles,
    IReadOnlyList<string> OversizedFiles,
    int FilesOverLimit,
    IReadOnlyList<string> Errors);
