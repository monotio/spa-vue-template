using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace VueApp1.Server.Agent;

public enum AgentToolTier
{
    /// <summary>ReadOnlyHint=true: auto-executes.</summary>
    ReadOnly,

    /// <summary>Annotated non-destructive write: approval per <c>Agent:RequireApprovalForWrites</c>.</summary>
    Write,

    /// <summary>DestructiveHint=true OR unannotated: approval, fail-closed.</summary>
    Destructive,
}

public sealed record AgentToolRegistration(
    McpServerToolAIFunction Function,
    AgentToolTier Tier,
    bool RequiresApproval);

/// <summary>
/// Derives execution tiers from the SAME MCP annotations the registry already
/// mandates for external trust UIs (docs/MCP.md's five-annotations doctrine
/// gains its second consumer): <c>ReadOnlyHint=true</c> auto-executes;
/// annotated non-destructive writes need approval by default (loosen via
/// <c>Agent:RequireApprovalForWrites</c>); destructive OR UNANNOTATED tools
/// always need approval — the MCP spec itself defaults <c>destructiveHint</c>
/// to true, so an unannotated tool is treated as dangerous, fail-closed.
///
/// Also owns two cache-discipline invariants as code, not convention:
/// the catalog is ordered deterministically (ordinal by name) and exposed as
/// read-only lists that are built once and NEVER mutated — a reordered or
/// mutated tools[] busts the provider's prompt-cache prefix for every
/// conversation in flight.
/// </summary>
public sealed class AgentToolPolicy
{
    private readonly Dictionary<string, AgentToolRegistration> _byName;

    public AgentToolPolicy(
        IEnumerable<McpServerTool> registry,
        McpToolAdapter adapter,
        IOptions<AgentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);

        var requireApprovalForWrites = options.Value.RequireApprovalForWrites;

        // Deterministic, byte-stable ordering: ordinal by tool name. DI
        // enumeration order is registration order — an implementation detail
        // a refactor may shuffle; the model-visible catalog must not shuffle
        // with it.
        Catalog = [.. registry
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .Select(tool =>
            {
                var tier = DeriveTier(tool);
                return new AgentToolRegistration(
                    adapter.Bridge(tool),
                    tier,
                    RequiresApprovalFor(tier, requireApprovalForWrites));
            })];

        _byName = new Dictionary<string, AgentToolRegistration>(StringComparer.Ordinal);
        foreach (var registration in Catalog)
        {
            if (!_byName.TryAdd(registration.Function.Name, registration))
            {
                throw new InvalidOperationException(
                    $"Duplicate MCP tool name '{registration.Function.Name}' — tool names are the "
                    + "dispatch and approval key and must be globally unique.");
            }
        }

        Tools = new ReadOnlyCollection<AITool>([.. Catalog.Select(AITool (r) => r.Function)]);
        UnattendedTools = new ReadOnlyCollection<AITool>(
            [.. Catalog.Where(r => r.Tier == AgentToolTier.ReadOnly).Select(AITool (r) => r.Function)]);
        PolicySurfaceHash = ComputePolicySurfaceHash(Catalog, requireApprovalForWrites);
    }

    public IReadOnlyList<AgentToolRegistration> Catalog { get; }

    /// <summary>The full ordered toolset for interactive requests. Never mutated.</summary>
    public IList<AITool> Tools { get; }

    /// <summary>
    /// The unattended posture: read-only tier ONLY. Detached/scheduled runs
    /// have no human to approve, so approval-tier tools are not advertised at
    /// all, and a hallucinated call to one gets the <c>not_authorized</c>
    /// envelope instead of parking the run.
    /// </summary>
    public IList<AITool> UnattendedTools { get; }

    /// <summary>
    /// Hash of the POLICY SURFACE, not just a toolset fingerprint: ordered
    /// tool names + schema bytes + tier assignments + the approval knob.
    /// A <see cref="PendingApproval"/> freezes this value; execution
    /// re-validates it and fails closed (409) on divergence — an approval
    /// granted under one surface must never execute under another
    /// (the privilege-drift lesson, ported before auth exists).
    /// </summary>
    public string PolicySurfaceHash { get; }

    public bool TryGet(string toolName, out AgentToolRegistration registration) =>
        _byName.TryGetValue(toolName, out registration!);

    public bool IsAllowedUnattended(string toolName) =>
        _byName.TryGetValue(toolName, out var registration)
        && registration.Tier == AgentToolTier.ReadOnly;

    private static AgentToolTier DeriveTier(McpServerTool tool)
    {
        var annotations = tool.ProtocolTool.Annotations;
        if (annotations?.ReadOnlyHint == true)
        {
            return AgentToolTier.ReadOnly;
        }

        // Only an EXPLICIT destructiveHint=false earns the milder write tier;
        // absent annotations (or absent destructiveHint) fall through to
        // Destructive — fail closed, matching the spec's own default.
        return annotations?.DestructiveHint == false
            ? AgentToolTier.Write
            : AgentToolTier.Destructive;
    }

    private static bool RequiresApprovalFor(AgentToolTier tier, bool requireApprovalForWrites) => tier switch
    {
        AgentToolTier.ReadOnly => false,
        AgentToolTier.Write => requireApprovalForWrites,
        _ => true,
    };

    private static string ComputePolicySurfaceHash(
        IReadOnlyList<AgentToolRegistration> catalog, bool requireApprovalForWrites)
    {
        var builder = new StringBuilder();
        builder.Append("RequireApprovalForWrites=").Append(requireApprovalForWrites).Append('\n');
        foreach (var registration in catalog)
        {
            builder
                .Append(registration.Function.Name).Append('\u001f')
                .Append(registration.Function.JsonSchema.GetRawText()).Append('\u001f')
                .Append(registration.Tier).Append('\u001f')
                .Append(registration.RequiresApproval).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
