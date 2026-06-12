using Microsoft.Extensions.AI;

namespace VueApp1.Server.Agent;

/// <summary>
/// Streaming state for start/delta/end framing: text and reasoning blocks
/// open on their first delta and close when the content kind switches (or
/// the response ends). Tool calls and results are atomic parts — the
/// adapters do not reliably surface argument deltas, which is exactly why
/// <c>tool-input-start/-delta</c> are not in the wire union.
/// </summary>
internal sealed class StreamingPartEmitter(string conversationId, Guid turnId)
{
    private BlockKind _open = BlockKind.None;

    public IEnumerable<AgentStreamPart> Map(AIContent content)
    {
        switch (content)
        {
            case TextContent { Text.Length: > 0 } text:
                if (_open != BlockKind.Text)
                {
                    foreach (var part in CloseOpenBlocks())
                    {
                        yield return part;
                    }

                    _open = BlockKind.Text;
                    yield return new TextStartPart { ConversationId = conversationId, TurnId = turnId };
                }

                yield return new TextDeltaPart { ConversationId = conversationId, TurnId = turnId, Delta = text.Text };
                break;

            case TextReasoningContent { Text.Length: > 0 } reasoning:
                if (_open != BlockKind.Reasoning)
                {
                    foreach (var part in CloseOpenBlocks())
                    {
                        yield return part;
                    }

                    _open = BlockKind.Reasoning;
                    yield return new ReasoningStartPart { ConversationId = conversationId, TurnId = turnId };
                }

                yield return new ReasoningDeltaPart { ConversationId = conversationId, TurnId = turnId, Delta = reasoning.Text };
                break;

            case FunctionCallContent call:
                foreach (var part in CloseOpenBlocks())
                {
                    yield return part;
                }

                yield return AgentUiParts.ToolInput(call, conversationId, turnId);
                break;

            default:
                break;
        }
    }

    public IEnumerable<AgentStreamPart> CloseOpenBlocks()
    {
        switch (_open)
        {
            case BlockKind.Text:
                _open = BlockKind.None;
                yield return new TextEndPart { ConversationId = conversationId, TurnId = turnId };
                break;
            case BlockKind.Reasoning:
                _open = BlockKind.None;
                yield return new ReasoningEndPart { ConversationId = conversationId, TurnId = turnId };
                break;
            default:
                break;
        }
    }

    private enum BlockKind
    {
        None,
        Text,
        Reasoning,
    }
}
