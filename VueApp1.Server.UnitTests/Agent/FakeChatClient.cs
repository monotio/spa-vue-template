using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Scripted <see cref="IChatClient"/> — the zero-secrets provider seam for
/// both test projects (compile-linked into IntegrationTests). Each enqueued
/// call yields its updates in order, then optionally hangs awaiting
/// cancellation (abort tests) or throws (provider-failure tests). Every call
/// records a snapshot of what the loop sent (messages, tools, tool mode) so
/// tests can pin cache discipline, posture filtering and replay shape.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly List<ScriptedCall> _script = [];
    private readonly object _gate = new();
    private int _callIndex;

    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    private readonly List<RecordedCall> _calls = [];

    public FakeChatClient Enqueue(params ChatResponseUpdate[] updates)
    {
        _script.Add(new ScriptedCall([.. updates], Hang: false, Exception: null));
        return this;
    }

    /// <summary>Yields the updates, then hangs until the call's CancellationToken fires.</summary>
    public FakeChatClient EnqueueHangingAfter(params ChatResponseUpdate[] updates)
    {
        _script.Add(new ScriptedCall([.. updates], Hang: true, Exception: null));
        return this;
    }

    /// <summary>Yields the updates, then throws <paramref name="exception"/>.</summary>
    public FakeChatClient EnqueueFailureAfter(Exception exception, params ChatResponseUpdate[] updates)
    {
        _script.Add(new ScriptedCall([.. updates], Hang: false, Exception: exception));
        return this;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ScriptedCall call;
        lock (_gate)
        {
            if (_callIndex >= _script.Count)
            {
                throw new InvalidOperationException(
                    $"FakeChatClient script exhausted: call #{_callIndex + 1} was made but only "
                    + $"{_script.Count} call(s) were enqueued.");
            }

            call = _script[_callIndex++];
            _calls.Add(new RecordedCall(
                [.. messages],
                options?.Tools is { } tools ? [.. tools] : [],
                options?.ToolMode));
        }

        foreach (var update in call.Updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }

        if (call.Exception is not null)
        {
            throw call.Exception;
        }

        if (call.Hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to dispose; the loop treats IChatClient as externally owned.
    }

    // -- Update builders -----------------------------------------------------

    public static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    public static ChatResponseUpdate Reasoning(string text) =>
        new(ChatRole.Assistant, [new TextReasoningContent(text)]);

    public static ChatResponseUpdate ToolCall(
        string callId, string name, IDictionary<string, object?>? arguments = null) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]);

    public static ChatResponseUpdate Usage(
        long inputTokens, long outputTokens, long cachedInputTokens = 0, long reasoningTokens = 0) =>
        new(ChatRole.Assistant,
        [
            new UsageContent(new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                CachedInputTokenCount = cachedInputTokens,
                ReasoningTokenCount = reasoningTokens,
            }),
        ]);

    private sealed record ScriptedCall(
        IReadOnlyList<ChatResponseUpdate> Updates, bool Hang, Exception? Exception);
}

/// <summary>What the loop actually sent on one provider call.</summary>
public sealed record RecordedCall(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AITool> Tools,
    ChatToolMode? ToolMode);
