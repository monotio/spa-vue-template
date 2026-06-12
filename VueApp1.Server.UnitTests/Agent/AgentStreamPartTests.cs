using System.Text.Json;
using Microsoft.Extensions.AI;
using VueApp1.Server.Agent;
using VueApp1.Server.Agent.Attachments;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Wire-shape snapshots for the SSE part union: camelCase property names,
/// the kebab-case <c>type</c> discriminator, identity fields
/// (<c>conversationId</c>/<c>turnId</c>) on EVERY part, and lossless
/// round-trips. The frontend composable parses exactly these bytes — this
/// file plus its frontend twin are the contract.
/// </summary>
public class AgentStreamPartTests
{
    private const string ConversationId = "conv-1";
    private static readonly Guid _turnId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static TheoryData<AgentStreamPart, string, string> Parts() => new()
    {
        {
            new TextStartPart { ConversationId = ConversationId, TurnId = _turnId },
            "text-start", "type,conversationId,turnId"
        },
        {
            new TextDeltaPart { ConversationId = ConversationId, TurnId = _turnId, Delta = "Hi" },
            "text-delta", "type,delta,conversationId,turnId"
        },
        {
            new TextEndPart { ConversationId = ConversationId, TurnId = _turnId },
            "text-end", "type,conversationId,turnId"
        },
        {
            new ReasoningStartPart { ConversationId = ConversationId, TurnId = _turnId },
            "reasoning-start", "type,conversationId,turnId"
        },
        {
            new ReasoningDeltaPart { ConversationId = ConversationId, TurnId = _turnId, Delta = "mull" },
            "reasoning-delta", "type,delta,conversationId,turnId"
        },
        {
            new ReasoningEndPart { ConversationId = ConversationId, TurnId = _turnId },
            "reasoning-end", "type,conversationId,turnId"
        },
        {
            new ToolInputAvailablePart
            {
                ConversationId = ConversationId, TurnId = _turnId,
                ToolCallId = "call-1", ToolName = "get_weather_forecast", ArgumentsJson = "{}",
            },
            "tool-input-available", "type,toolCallId,toolName,argumentsJson,conversationId,turnId"
        },
        {
            new ToolOutputAvailablePart
            {
                ConversationId = ConversationId, TurnId = _turnId,
                ToolCallId = "call-1", ResultJson = """{"result":[]}""", IsError = false,
            },
            "tool-output-available", "type,toolCallId,resultJson,isError,conversationId,turnId"
        },
        {
            new ToolApprovalRequiredPart
            {
                ConversationId = ConversationId, TurnId = _turnId,
                ToolCallId = "call-2", ToolName = "delete_item", ArgumentsJson = """{"id":7}""",
            },
            "tool-approval-required", "type,toolCallId,toolName,argumentsJson,conversationId,turnId"
        },
        {
            new FilePart
            {
                ConversationId = ConversationId, TurnId = _turnId,
                AttachmentId = "att-1", FileName = "notes.txt", MediaType = "text/plain",
            },
            "file", "type,attachmentId,fileName,mediaType,conversationId,turnId"
        },
        {
            new UsagePart
            {
                ConversationId = ConversationId, TurnId = _turnId,
                InputTokens = 100, CachedInputTokens = 40, OutputTokens = 20, ReasoningTokens = 5, EstimatedUsd = 0.0123m,
            },
            "usage", "type,inputTokens,cachedInputTokens,outputTokens,reasoningTokens,estimatedUsd,conversationId,turnId"
        },
        {
            new ErrorPart
            {
                ConversationId = ConversationId, TurnId = _turnId,
                Problem = new AgentProblem("/problems/x", "Boom", 502, "It broke."),
            },
            "error", "type,problem,conversationId,turnId"
        },
        {
            new FinishPart { ConversationId = ConversationId, TurnId = _turnId, Reason = "stop" },
            "finish", "type,reason,conversationId,turnId"
        },
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void EveryPart_SerializesWithDiscriminatorIdentityAndCamelCase(
        AgentStreamPart part, string expectedType, string expectedProperties)
    {
        var json = JsonSerializer.Serialize(part, AgentJsonContext.Default.AgentStreamPart);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(expectedType, root.GetProperty("type").GetString());
        // Identity on EVERY part (late events from a dead run must be
        // attributable and droppable client-side).
        Assert.Equal(ConversationId, root.GetProperty("conversationId").GetString());
        Assert.Equal(_turnId, root.GetProperty("turnId").GetGuid());
        // Exact property set — no extras, no renames, camelCase only.
        var actual = root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(expectedProperties.Split(',').Order(StringComparer.Ordinal), actual);
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void EveryPart_RoundTripsLosslessly(
        AgentStreamPart part, string expectedType, string expectedProperties)
    {
        _ = expectedType;
        _ = expectedProperties;
        var json = JsonSerializer.Serialize(part, AgentJsonContext.Default.AgentStreamPart);
        var roundTripped = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentStreamPart);

        Assert.Equal(part, roundTripped);
    }

    [Fact]
    public void TextDelta_ExactWireBytes()
    {
        // One literal byte-level snapshot so an accidental serializer-config
        // change (naming policy, discriminator name) cannot slip through the
        // structural assertions above.
        var json = JsonSerializer.Serialize<AgentStreamPart>(
            new TextDeltaPart { ConversationId = "c1", TurnId = _turnId, Delta = "Hi" },
            AgentJsonContext.Default.AgentStreamPart);

        Assert.StartsWith("""{"type":"text-delta",""", json, StringComparison.Ordinal);
        Assert.Contains(""""delta":"Hi"""", json, StringComparison.Ordinal);
        Assert.Contains(""""conversationId":"c1"""", json, StringComparison.Ordinal);
        Assert.Contains($"\"turnId\":\"{_turnId}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorPart_NestsAProblemDetailsShapedObject()
    {
        var json = JsonSerializer.Serialize<AgentStreamPart>(
            new ErrorPart
            {
                ConversationId = ConversationId,
                TurnId = _turnId,
                Problem = new AgentProblem(null, "Boom", 502, "It broke."),
            },
            AgentJsonContext.Default.AgentStreamPart);
        using var document = JsonDocument.Parse(json);
        var problem = document.RootElement.GetProperty("problem");

        Assert.Equal("Boom", problem.GetProperty("title").GetString());
        Assert.Equal(502, problem.GetProperty("status").GetInt32());
        Assert.Equal("It broke.", problem.GetProperty("detail").GetString());
        // Null type is omitted, not emitted as null.
        Assert.False(problem.TryGetProperty("type", out _));
    }

    [Fact]
    public void ReplayMapper_DerivesTheSamePartShapesTheLiveStreamEmits()
    {
        // One source of truth: a completed assistant message replays as
        // start → delta → end framing plus atomic tool parts — the exact
        // vocabulary the live loop emits, so one client renderer serves both.
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("thinking..."),
            new TextContent("Here you go."),
            new FunctionCallContent("call-1", "get_weather_forecast", new Dictionary<string, object?>()),
        ]);

        var parts = AgentUiParts.FromMessage(message, ConversationId, _turnId).ToList();

        Assert.Collection(
            parts,
            part => Assert.IsType<ReasoningStartPart>(part),
            part => Assert.Equal("thinking...", Assert.IsType<ReasoningDeltaPart>(part).Delta),
            part => Assert.IsType<ReasoningEndPart>(part),
            part => Assert.IsType<TextStartPart>(part),
            part => Assert.Equal("Here you go.", Assert.IsType<TextDeltaPart>(part).Delta),
            part => Assert.IsType<TextEndPart>(part),
            part => Assert.Equal("get_weather_forecast", Assert.IsType<ToolInputAvailablePart>(part).ToolName));
        Assert.All(parts, part =>
        {
            Assert.Equal(ConversationId, part.ConversationId);
            Assert.Equal(_turnId, part.TurnId);
        });
    }

    [Fact]
    public void ReplayMapper_DerivesFilePartsFromStampedReferences_NeverFromBytes()
    {
        // A user message persists attachment REFERENCES in app-level state
        // (P1); the snapshot derives `file` parts from that stamp — the chips
        // replay even after the in-memory blob store evicted the bytes.
        var message = new ChatMessage(ChatRole.User, "look at these")
        {
            AdditionalProperties = new()
            {
                [AgentUiParts.AttachmentsStampKey] = (IReadOnlyList<AgentAttachmentRef>)
                [
                    new AgentAttachmentRef("att-1", "pic.png", "image/png"),
                    new AgentAttachmentRef("att-2", "notes.txt", "text/plain"),
                ],
            },
        };

        var parts = AgentUiParts.FromMessage(message, ConversationId, _turnId).ToList();

        Assert.Collection(
            parts,
            part => Assert.IsType<TextStartPart>(part),
            part => Assert.Equal("look at these", Assert.IsType<TextDeltaPart>(part).Delta),
            part => Assert.IsType<TextEndPart>(part),
            part =>
            {
                var file = Assert.IsType<FilePart>(part);
                Assert.Equal("att-1", file.AttachmentId);
                Assert.Equal("pic.png", file.FileName);
                Assert.Equal("image/png", file.MediaType);
            },
            part => Assert.Equal("notes.txt", Assert.IsType<FilePart>(part).FileName));
    }

    [Fact]
    public void ReplayMapper_MarksErrorEnvelopeResults()
    {
        var result = new FunctionResultContent("call-1", """{"code":"not_found"}""")
        {
            AdditionalProperties = new() { [AgentUiParts.ToolErrorStampKey] = true },
        };
        var message = new ChatMessage(ChatRole.Tool, [result]);

        var part = Assert.IsType<ToolOutputAvailablePart>(
            Assert.Single(AgentUiParts.FromMessage(message, ConversationId, _turnId)));

        Assert.True(part.IsError);
        Assert.Equal("""{"code":"not_found"}""", part.ResultJson);
    }
}
