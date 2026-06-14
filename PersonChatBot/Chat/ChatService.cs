using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using PersonChatBot.Configuration;
using PersonChatBot.Models;

namespace PersonChatBot.Chat;

/// <summary>
/// Answers questions strictly from retrieved document context, streaming tokens
/// from the local model and surfacing the sources it was given.
/// </summary>
public sealed class ChatService
{
    public const string NoContextMessage =
        "I couldn't find anything relevant in your documents to answer that. " +
        "Try rephrasing, or check that the document is in the watched folder and indexed.";

    private const string SystemPrompt =
        """
        You are a helpful assistant that answers questions using ONLY the provided sources.
        Rules:
        - Use only the information in the SOURCES block. Do not use outside knowledge.
        - If the sources do not contain the answer, say you don't know — do not guess.
        - Cite the sources you use inline, like [1] or [2], matching the numbered sources.
        - Be concise and quote figures or names exactly as they appear.
        """;

    private readonly IChatCompletionService _chat;
    private readonly RetrievalService _retrieval;
    private readonly RagOptions _options;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IChatCompletionService chat,
        RetrievalService retrieval,
        IOptions<RagOptions> options,
        ILogger<ChatService> logger)
    {
        _chat = chat;
        _retrieval = retrieval;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Retrieve the chunks that will ground the answer (shown as citations in the UI).</summary>
    public Task<IReadOnlyList<SearchHit>> RetrieveContextAsync(string question, CancellationToken ct = default)
        => _retrieval.RetrieveAsync(question, ct);

    /// <summary>
    /// Stream the grounded answer token-by-token. If no context was found, yields a
    /// single graceful message and never calls the model.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string question,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<SearchHit> context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (context.Count == 0)
        {
            yield return NoContextMessage;
            yield break;
        }

        var chatHistory = BuildChatHistory(question, history, context);
        var settings = new OllamaPromptExecutionSettings { Temperature = _options.Temperature };

        await foreach (var token in _chat.GetStreamingChatMessageContentsAsync(
                           chatHistory, settings, cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(token.Content))
                yield return token.Content;
        }
    }

    private ChatHistory BuildChatHistory(
        string question, IReadOnlyList<ChatTurn> history, IReadOnlyList<SearchHit> context)
    {
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(SystemPrompt);

        // Only keep the most recent turns: the prompt also carries the system
        // message and the retrieved context, and the local model has a limited
        // context window. Older turns are dropped to avoid overflowing it.
        var recent = _options.MaxHistoryTurns > 0 && history.Count > _options.MaxHistoryTurns
            ? history.Skip(history.Count - _options.MaxHistoryTurns)
            : history;

        foreach (var turn in recent)
        {
            if (turn.Author == ChatAuthor.User)
                chatHistory.AddUserMessage(turn.Content);
            else
                chatHistory.AddAssistantMessage(turn.Content);
        }

        chatHistory.AddUserMessage(BuildGroundedPrompt(question, context));
        return chatHistory;
    }

    private static string BuildGroundedPrompt(string question, IReadOnlyList<SearchHit> context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SOURCES:");
        for (var i = 0; i < context.Count; i++)
        {
            var hit = context[i];
            var location = hit.Page > 0 ? $"{hit.FileName}, p.{hit.Page}" : hit.FileName;
            sb.AppendLine($"[{i + 1}] ({location})");
            sb.AppendLine(hit.Text);
            sb.AppendLine();
        }

        sb.AppendLine("QUESTION:");
        sb.AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("Answer using only the sources above, citing them inline like [1]. " +
                      "If they don't contain the answer, say you don't know.");
        return sb.ToString();
    }
}
