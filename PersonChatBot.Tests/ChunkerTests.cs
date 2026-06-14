using PersonChatBot.Configuration;
using PersonChatBot.Ingestion;
using PersonChatBot.Models;

namespace PersonChatBot.Tests;

public class ChunkerTests
{
    private static Chunker MakeChunker(int chunkTokens, int overlapTokens) =>
        new(TestSupport.Options(new RagOptions
        {
            ChunkSizeTokens = chunkTokens,
            ChunkOverlapTokens = overlapTokens,
        }));

    [Fact]
    public void Short_text_becomes_a_single_chunk()
    {
        var chunker = MakeChunker(chunkTokens: 750, overlapTokens: 100);
        var pages = new List<ExtractedPage> { new(1, "A short sentence.") };

        var chunks = chunker.Chunk(pages);

        var chunk = Assert.Single(chunks);
        Assert.Equal("A short sentence.", chunk.Text);
        Assert.Equal(1, chunk.Page);
        Assert.Equal(0, chunk.ChunkIndex);
    }

    [Fact]
    public void Empty_or_whitespace_pages_produce_no_chunks()
    {
        var chunker = MakeChunker(750, 100);
        var pages = new List<ExtractedPage> { new(1, "   \n\t  "), new(2, "") };

        Assert.Empty(chunker.Chunk(pages));
    }

    [Fact]
    public void Long_text_is_split_into_multiple_overlapping_chunks()
    {
        // Tiny budget (10 tokens ~= 40 chars) forces several chunks.
        var chunker = MakeChunker(chunkTokens: 10, overlapTokens: 2);
        var text = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"word{i}"));
        var pages = new List<ExtractedPage> { new(1, text) };

        var chunks = chunker.Chunk(pages);

        Assert.True(chunks.Count > 1, "expected the long text to split into multiple chunks");
        // Indices are sequential starting at 0.
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.ChunkIndex));
        // Every chunk carries the page number.
        Assert.All(chunks, c => Assert.Equal(1, c.Page));
    }

    [Fact]
    public void Consecutive_chunks_overlap()
    {
        var chunker = MakeChunker(chunkTokens: 10, overlapTokens: 4);
        var text = string.Join(" ", Enumerable.Range(0, 40).Select(i => $"token{i:00}"));
        var pages = new List<ExtractedPage> { new(1, text) };

        var chunks = chunker.Chunk(pages);

        // The end of one chunk should reappear at the start of the next.
        var firstTail = chunks[0].Text.Split(' ').Last();
        Assert.Contains(firstTail, chunks[1].Text.Split(' '));
    }

    [Fact]
    public void Chunks_never_span_pages_so_page_numbers_stay_accurate()
    {
        var chunker = MakeChunker(750, 100);
        var pages = new List<ExtractedPage>
        {
            new(1, "Content that belongs to page one."),
            new(2, "Different content that belongs to page two."),
        };

        var chunks = chunker.Chunk(pages);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks[0].Page);
        Assert.Equal(2, chunks[1].Page);
        Assert.Contains("page one", chunks[0].Text);
        Assert.Contains("page two", chunks[1].Text);
    }

    [Fact]
    public void Whitespace_is_normalized()
    {
        var chunker = MakeChunker(750, 100);
        var pages = new List<ExtractedPage> { new(1, "lots   of\n\n\twhitespace") };

        var chunk = Assert.Single(chunker.Chunk(pages));
        Assert.Equal("lots of whitespace", chunk.Text);
    }
}
