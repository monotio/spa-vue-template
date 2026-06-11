using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace VueApp1.Server.Mcp;

/// <summary>
/// Machine-readable error envelope for MCP tool failures — the agent-surface
/// twin of the RFC 9457 problem details the REST API emits. <c>Code</c> is a
/// finite, stable vocabulary agents can branch on; the optional members carry
/// the same <see cref="ProblemDetailTypes"/> URI, title and detail the HTTP
/// response for the same failure would have.
/// </summary>
public sealed record McpToolError(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

/// <summary>
/// Shapes every MCP tool outcome at the protocol level — the MCP twin of
/// <c>ApiControllerBase.HandleServiceResponse</c>. Route all tool returns
/// through <see cref="FromServiceResponse{T}"/> and new tools are correct by
/// construction. The trap this encodes: a failure returned as a successful
/// JSON string is INVISIBLE to the protocol — MCP runtimes branch on
/// <c>isError</c>, and an error-shaped success sends agents into retry loops.
/// </summary>
public static class McpToolResults
{
    // Same camelCase wire casing as the REST API, so a value observed through
    // a tool call matches the one observed through the HTTP endpoint.
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps a <see cref="ServiceResponse{T}"/> onto a protocol-level tool
    /// result: success serializes the value, failure becomes an
    /// <c>isError</c> result carrying the <see cref="McpToolError"/> envelope.
    /// </summary>
    public static CallToolResult FromServiceResponse<T>(ServiceResponse<T> response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.IsSuccess ? Success(response.Value) : Error(response);
    }

    /// <summary>
    /// Dual emission: <c>structuredContent</c> for typed consumers, and the
    /// same JSON as text content for runtimes that only surface text blocks.
    /// For POCO-returning tools, prefer the SDK's
    /// <c>UseStructuredContent = true</c> (it also generates an
    /// <c>outputSchema</c>); this helper is for tools that return
    /// <see cref="CallToolResult"/> because they route failures through the
    /// envelope.
    /// </summary>
    public static CallToolResult Success<T>(T value)
    {
        var json = JsonSerializer.SerializeToElement(value, _serializerOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json.GetRawText() }],
            StructuredContent = json,
        };
    }

    /// <summary>
    /// Marks the result as a protocol-level error (<c>isError: true</c>) and
    /// serializes the <see cref="McpToolError"/> envelope as text content.
    /// Deliberately NO <c>structuredContent</c> on errors: a tool that
    /// declares an <c>outputSchema</c> must not emit structured content that
    /// violates it, and the envelope is not the tool's output shape.
    /// </summary>
    public static CallToolResult Error(ServiceResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccess)
        {
            throw new ArgumentException(
                "Cannot build an MCP error result from a successful ServiceResponse.",
                nameof(response));
        }

        var envelope = new McpToolError(
            Code: MapCode(response),
            Status: response.Details?.Status,
            Type: response.Details?.Type,
            Title: response.Details?.Title,
            Detail: response.Details?.Detail);

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, _serializerOptions) }],
        };
    }

    /// <summary>
    /// Mirrors <c>ApiControllerBase.HandleServiceResponse</c>: the same
    /// <see cref="ServiceResult"/> that picks the HTTP status there picks the
    /// stable code here (412 rides on BadRequest via Details.Status, exactly
    /// like the controller path).
    /// </summary>
    private static string MapCode(ServiceResponse response) => response.Result switch
    {
        ServiceResult.BadRequest when response.Details?.Status == StatusCodes.Status412PreconditionFailed =>
            "precondition_failed",
        ServiceResult.BadRequest => "invalid_parameter",
        ServiceResult.NotFound => "not_found",
        ServiceResult.Conflict => "conflict",
        ServiceResult.NotAuthorized => "not_authorized",
        ServiceResult.TooManyRequests => "rate_limited",
        _ => throw new InvalidOperationException($"Unhandled service result: {response.Result}"),
    };
}
