using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PersonChatBot.Chat;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;
using PersonChatBot.Models;
using PersonChatBot.Storage;

namespace PersonChatBot.Tests;

public class ChatServiceTests
{
    private static ChatService MakeChatService(IChatCompletionService chat, RagOptions? ragOptions = null)
    {
        var options = TestSupport.Options(ragOptions ?? new RagOptions { EmbeddingDimensions = 4 });
        var store = new SqliteVecStore(options, NullLogger<SqliteVecStore>.Instance); // never used here
        var retrieval = new RetrievalService(TestSupport.Embedder(4), store, options);
        return new ChatService(chat, retrieval, options, NullLogger<ChatService>.Instance);
    }

    private static SearchHit Hit(string text) => new(@"C:\doc.txt", "doc.txt", 1, 0, text, 0.9);

    [Fact]
    public async Task With_no_context_it_returns_the_graceful_message_and_never_calls_the_model()
    {
        var chat = MakeChatService(new ThrowingChatService());

        var output = new List<string>();
        await foreach (var token in chat.StreamAnswerAsync("anything", [], context: []))
            output.Add(token);

        // Single graceful message, and the model was never invoked (would have thrown).
        Assert.Equal(ChatService.NoContextMessage, Assert.Single(output));
    }

    [Fact]
    public async Task With_context_it_streams_the_models_tokens()
    {
        var chat = MakeChatService(new ScriptedChatService("Paris ", "is ", "the capital."));

        var answer = "";
        await foreach (var token in chat.StreamAnswerAsync(
                           "What is the capital of France?", [], context: [Hit("France's capital is Paris.")]))
            answer += token;

        Assert.Equal("Paris is the capital.", answer);
    }

    [Fact]
    public async Task History_is_trimmed_to_the_configured_number_of_turns()
    {
        var scripted = new ScriptedChatService("ok");
        var chat = MakeChatService(scripted, new RagOptions { EmbeddingDimensions = 4, MaxHistoryTurns = 2 });

        var history = new List<ChatTurn>
        {
            new(ChatAuthor.User, "q1"), new(ChatAuthor.Assistant, "a1"),
            new(ChatAuthor.User, "q2"), new(ChatAuthor.Assistant, "a2"),
            new(ChatAuthor.User, "q3"), new(ChatAuthor.Assistant, "a3"),
        };

        await foreach (var _ in chat.StreamAnswerAsync("now", history, [Hit("some context")])) { }

        var sent = scripted.LastHistory!;
        // system prompt + last 2 history turns + current grounded question = 4 messages.
        Assert.Equal(4, sent.Count);
        // The kept turns are the most recent ones; "q1"/"a1"/"q2"/"a2" are dropped.
        Assert.Contains(sent, m => m.Content == "a3");
        Assert.DoesNotContain(sent, m => m.Content == "q1");
    }

    // --- fakes ---

    private sealed class ThrowingChatService : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("model should not be called when there is no context");

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("model should not be called when there is no context");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class ScriptedChatService : IChatCompletionService
    {
        private readonly string[] _tokens;
        public ScriptedChatService(params string[] tokens) => _tokens = tokens;

        public ChatHistory? LastHistory { get; private set; }

        public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ChatMessageContent>>(
                [new ChatMessageContent(AuthorRole.Assistant, string.Concat(_tokens))]);

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastHistory = chatHistory;
            foreach (var token in _tokens)
            {
                await Task.Yield();
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, token);
            }
        }
    }
}
