---
globs:
  - 'VueApp1.Server/Mcp/**'
---

# MCP rules (auto-loaded when touching the MCP module)

- Full doctrine: docs/MCP.md. Key points below.
- Tools delegate to the EXISTING `ServiceResponse<T>` service layer — never
  inline business logic or duplicate what a controller already calls.
- Route every tool return through `McpToolResults.FromServiceResponse`.
  Never return an error as a successful JSON string: runtimes branch on
  `isError`, and an error-shaped success sends agents into retry loops.
- Error `code` values are a stable wire contract (asserted as literals in
  `McpToolResultsTests`) — renaming one is a breaking change for connected
  agents.
- Set ALL FIVE annotations explicitly on every `[McpServerTool]`: `Title`,
  `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`. The spec defaults
  destructive/open-world to TRUE for unannotated tools.
- `CallToolResult`-returning tools advertise their success shape with
  `UseStructuredContent = true` + `OutputSchemaType` (see `WeatherTools`).
  `structuredContent` must be a JSON OBJECT: `McpToolResults.Success` wraps
  non-object values as `{ "result": ... }`, matching the SDK's schema wrapper.
- Descriptions: purpose + limitations + usage guidance + exact
  parameter/return formats (enum values listed literally).
- Nullable parameter without `= null` is REQUIRED in the generated schema
  (SDK 1.4.0 behavior — re-verify on SDK bumps).
- New tool classes need a `WithTools<T>()` registration in `SetupMcp`
  (Program.cs) and SDK-level coverage in `McpEndpointTests`.
