using System.Text.Json;
using ModelContextProtocol.Protocol;
using VueApp1.Server.Mcp;
using Xunit;

namespace VueApp1.Server.UnitTests.Mcp;

/// <summary>
/// Pins the agent-facing wire contract of the envelope: the stable code
/// vocabulary mirrors ApiControllerBase.HandleServiceResponse arm by arm, and
/// codes are asserted as LITERALS — renaming one is a breaking change for
/// every connected agent, and must show up here as a red test.
/// </summary>
public class McpToolResultsTests
{
    private static JsonElement ParseTextContent(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var document = JsonDocument.Parse(text.Text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Success_EmitsStructuredContentAndMatchingText()
    {
        var result = McpToolResults.Success(new WeatherForecast(new DateOnly(2026, 6, 11), 21, "Mild"));

        Assert.Null(result.IsError);
        Assert.NotNull(result.StructuredContent);

        // camelCase like the REST API, computed property included. An object
        // value is emitted as-is — no wrapper needed.
        var structured = result.StructuredContent.Value;
        Assert.Equal("2026-06-11", structured.GetProperty("date").GetString());
        Assert.Equal(21, structured.GetProperty("temperatureC").GetInt32());
        Assert.Equal(69, structured.GetProperty("temperatureF").GetInt32());
        Assert.Equal("Mild", structured.GetProperty("summary").GetString());

        // The text block is the SAME JSON, for runtimes that only read text.
        Assert.Equal(structured.GetRawText(), ParseTextContent(result).GetRawText());
    }

    [Fact]
    public void Success_NonObjectValue_WrapsStructuredContentInResult()
    {
        // structuredContent must be a JSON OBJECT (spec; SEP-2106 proposes
        // relaxing this): arrays/primitives are wrapped as { "result": ... },
        // the same wrapper the SDK bakes into OutputSchemaType schemas. The
        // text block stays the RAW value JSON.
        var result = McpToolResults.Success<IReadOnlyList<int>>([1, 2, 3]);

        Assert.NotNull(result.StructuredContent);
        var structured = result.StructuredContent.Value;
        Assert.Equal(JsonValueKind.Object, structured.ValueKind);
        Assert.Equal(3, structured.GetProperty("result").GetArrayLength());

        var text = ParseTextContent(result);
        Assert.Equal(JsonValueKind.Array, text.ValueKind);
        Assert.Equal(3, text.GetArrayLength());
    }

    [Fact]
    public void FromServiceResponse_Success_DelegatesToSuccess()
    {
        var result = McpToolResults.FromServiceResponse(ServiceResponse<string>.Success("value"));

        Assert.Null(result.IsError);
        // A primitive value rides inside the object wrapper.
        Assert.Equal("value", result.StructuredContent?.GetProperty("result").GetString());
    }

    [Fact]
    public void FromServiceResponse_NonGeneric_Success_EmitsMinimalOkResult()
    {
        // The valueless twin of the non-generic HandleServiceResponse
        // overload: command-style tools get a minimal success with no
        // structuredContent (there is no output shape to type).
        var result = McpToolResults.FromServiceResponse(ServiceResponse.Success());

        Assert.Null(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.True(ParseTextContent(result).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void FromServiceResponse_NonGeneric_Failure_SetsIsErrorWithEnvelope()
    {
        var result = McpToolResults.FromServiceResponse(ServiceResponse.Conflict("duplicate"));

        Assert.True(result.IsError);
        Assert.Equal("conflict", ParseTextContent(result).GetProperty("code").GetString());
    }

    [Fact]
    public void FromServiceResponse_Failure_SetsIsError()
    {
        var result = McpToolResults.FromServiceResponse(ServiceResponse<string>.NotFound());

        Assert.True(result.IsError);
        // No structuredContent on errors: the envelope is not the tool's
        // declared output shape, so it must not masquerade as one.
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public void Error_NotFound_MapsCode()
    {
        var envelope = ParseTextContent(McpToolResults.Error(ServiceResponse.NotFound()));

        Assert.Equal("not_found", envelope.GetProperty("code").GetString());
        // Null members are omitted, not emitted as JSON nulls.
        Assert.False(envelope.TryGetProperty("type", out _));
        Assert.False(envelope.TryGetProperty("detail", out _));
    }

    [Fact]
    public void Error_BadRequest_MapsCodeAndCarriesProblemFields()
    {
        var response = ServiceResponse.BadRequest(
            ProblemDetailTypes.ValidationFailed, "Invalid input", "broken");

        var result = McpToolResults.Error(response);
        var envelope = ParseTextContent(result);

        Assert.True(result.IsError);
        Assert.Equal("invalid_parameter", envelope.GetProperty("code").GetString());
        Assert.Equal(ProblemDetailTypes.ValidationFailed, envelope.GetProperty("type").GetString());
        Assert.Equal("Invalid input", envelope.GetProperty("title").GetString());
        Assert.Equal("broken", envelope.GetProperty("detail").GetString());
        Assert.Equal(400, envelope.GetProperty("status").GetInt32());
    }

    [Fact]
    public void Error_UnprocessableEntity_IsInvalidParameterWith422()
    {
        var envelope = ParseTextContent(
            McpToolResults.Error(ServiceResponse.UnprocessableEntity("semantically wrong")));

        Assert.Equal("invalid_parameter", envelope.GetProperty("code").GetString());
        Assert.Equal(ProblemDetailTypes.ValidationFailed, envelope.GetProperty("type").GetString());
        Assert.Equal(422, envelope.GetProperty("status").GetInt32());
    }

    [Fact]
    public void Error_PreconditionFailed_GetsOwnCode()
    {
        // 412 rides on the BadRequest result with Details.Status driving the
        // distinction — the same shape HandleServiceResponse branches on.
        var envelope = ParseTextContent(
            McpToolResults.Error(ServiceResponse.PreconditionFailed("stale version")));

        Assert.Equal("precondition_failed", envelope.GetProperty("code").GetString());
        Assert.Equal(412, envelope.GetProperty("status").GetInt32());
    }

    [Fact]
    public void Error_Conflict_MapsCode()
    {
        var envelope = ParseTextContent(
            McpToolResults.Error(ServiceResponse.Conflict(
                ProblemDetailTypes.ConflictingState, "Already exists", "duplicate")));

        Assert.Equal("conflict", envelope.GetProperty("code").GetString());
        Assert.Equal(ProblemDetailTypes.ConflictingState, envelope.GetProperty("type").GetString());
    }

    [Fact]
    public void Error_NotAuthorized_MapsCode()
    {
        var envelope = ParseTextContent(
            McpToolResults.Error(new ServiceResponse { Result = ServiceResult.NotAuthorized }));

        Assert.Equal("not_authorized", envelope.GetProperty("code").GetString());
    }

    [Fact]
    public void Error_TooManyRequests_MapsCode()
    {
        var envelope = ParseTextContent(
            McpToolResults.Error(new ServiceResponse { Result = ServiceResult.TooManyRequests }));

        Assert.Equal("rate_limited", envelope.GetProperty("code").GetString());
    }

    [Fact]
    public void Error_OnSuccessResponse_Throws()
    {
        Assert.Throws<ArgumentException>(() => McpToolResults.Error(ServiceResponse.Success()));
    }
}
