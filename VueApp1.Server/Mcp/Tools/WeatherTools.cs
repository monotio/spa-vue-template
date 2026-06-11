using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VueApp1.Server.Services;

namespace VueApp1.Server.Mcp.Tools;

/// <summary>
/// Sample MCP tool class. The pattern to copy: delegate to the SAME service
/// the REST controller uses (one source of truth — no REST/agent drift) and
/// route the <see cref="ServiceResponse{T}"/> through
/// <see cref="McpToolResults"/> so failures reach the agent as protocol-level
/// errors. Tool classes are instantiated per call with constructor injection;
/// register new ones in <c>SetupMcp</c> with <c>WithTools&lt;T&gt;()</c>.
/// Tool-design doctrine (annotations, descriptions, budget): docs/MCP.md.
/// </summary>
[McpServerToolType]
public sealed class WeatherTools(IWeatherForecastService weatherService)
{
    // All five annotations set EXPLICITLY: the MCP spec defaults
    // destructiveHint and openWorldHint to TRUE, so an unannotated read-only
    // tool presents to runtimes as dangerous and unpredictable.
    // OutputSchemaType (requires UseStructuredContent) advertises an
    // outputSchema even though the method returns CallToolResult directly:
    // the SDK wraps this non-object type as { "result": [...] } — the same
    // wrapper McpToolResults.Success() applies to the value, so the emitted
    // structuredContent always satisfies the advertised schema.
    [McpServerTool(
        Name = "get_weather_forecast",
        Title = "Get weather forecast",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(List<WeatherForecast>))]
    [Description(
        "Gets the five-day weather forecast. "
        + "Limitations: sample data — temperatures are randomly generated, results are cached for up to "
        + "30 seconds, and no location can be specified. "
        + "Use when the user asks about upcoming weather in this application. "
        + "Returns a JSON array of { date: 'YYYY-MM-DD', temperatureC: int, temperatureF: int, summary: string }; "
        + "in structuredContent the array is wrapped as { result: [...] } per the declared output schema.")]
    public async Task<CallToolResult> GetWeatherForecast(CancellationToken cancellationToken)
    {
        var response = await weatherService.GetForecastsAsync(cancellationToken);
        return McpToolResults.FromServiceResponse(response);
    }
}
