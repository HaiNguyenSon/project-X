using Microsoft.Extensions.AI;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;

namespace PersonChatBot.Tests;

public class EmbeddingServiceTests
{
    [Fact]
    public async Task Query_text_gets_the_query_prefix()
    {
        var capture = new CapturingGenerator(4);
        var service = new EmbeddingService(capture,
            TestSupport.Options(new RagOptions { EmbeddingQueryPrefix = "search_query: " }));

        await service.EmbedQueryAsync("how many vacation days?");

        Assert.Equal("search_query: how many vacation days?", Assert.Single(capture.Seen));
    }

    [Fact]
    public async Task Document_chunks_get_the_document_prefix()
    {
        var capture = new CapturingGenerator(4);
        var service = new EmbeddingService(capture,
            TestSupport.Options(new RagOptions { EmbeddingDocumentPrefix = "search_document: " }));

        await service.EmbedDocumentsAsync(["chunk one", "chunk two"]);

        Assert.Equal(["search_document: chunk one", "search_document: chunk two"], capture.Seen);
    }

    [Fact]
    public async Task Empty_document_list_returns_empty_without_calling_the_model()
    {
        var capture = new CapturingGenerator(4);
        var service = new EmbeddingService(capture, TestSupport.Options(new RagOptions()));

        Assert.Empty(await service.EmbedDocumentsAsync([]));
        Assert.Empty(capture.Seen);
    }

    [Fact]
    public async Task Documents_are_embedded_in_batches_and_results_keep_order()
    {
        var capture = new CapturingGenerator(4);
        var service = new EmbeddingService(capture,
            TestSupport.Options(new RagOptions { EmbeddingBatchSize = 2, EmbeddingDocumentPrefix = "" }));

        var texts = new[] { "a", "b", "c", "d", "e" };
        var vectors = await service.EmbedDocumentsAsync(texts);

        Assert.Equal(5, vectors.Count);                 // one vector per input
        Assert.Equal(3, capture.BatchCalls);            // 2 + 2 + 1
        Assert.Equal(texts, capture.Seen);              // order preserved across batches
    }

    [Fact]
    public async Task Connection_refused_surfaces_an_actionable_message()
    {
        var generator = new ThrowingGenerator(
            new HttpRequestException("No connection could be made because the target machine actively refused it."));
        var service = new EmbeddingService(generator,
            TestSupport.Options(new RagOptions
            {
                OllamaEndpoint = "http://localhost:11434",
                EmbeddingModel = "nomic-embed-text",
            }));

        var docEx = await Assert.ThrowsAsync<EmbeddingServiceUnavailableException>(
            () => service.EmbedDocumentsAsync(["chunk"]));
        var queryEx = await Assert.ThrowsAsync<EmbeddingServiceUnavailableException>(
            () => service.EmbedQueryAsync("question"));

        foreach (var message in new[] { docEx.Message, queryEx.Message })
        {
            Assert.Contains("nomic-embed-text", message);
            Assert.Contains("http://localhost:11434", message);
            Assert.Contains("ollama", message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.IsType<HttpRequestException>(docEx.InnerException);
    }

    [Fact]
    public async Task Non_connectivity_errors_are_not_wrapped()
    {
        var generator = new ThrowingGenerator(new InvalidOperationException("bad request"));
        var service = new EmbeddingService(generator, TestSupport.Options(new RagOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedDocumentsAsync(["chunk"]));
    }

    private sealed class ThrowingGenerator(Exception toThrow) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) => throw toThrow;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingGenerator(int dim) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Seen { get; } = [];
        public int BatchCalls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            var list = values.ToList();
            Seen.AddRange(list);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                list.Select(_ => new Embedding<float>(new float[dim])).ToList()));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
