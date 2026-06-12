using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using VueApp1.Server.Agent;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Pins the annotation-derived tiers (fail-closed for unannotated tools),
/// the deterministic byte-stable catalog ordering, and the policy-surface
/// hash sensitivity matrix.
/// </summary>
public class AgentToolPolicyTests
{
    [Fact]
    public void ReadOnlyTool_AutoExecutes()
    {
        var policy = CreatePolicy([Tool("read_tool", readOnly: true, destructive: false)]);

        var registration = Assert.Single(policy.Catalog);
        Assert.Equal(AgentToolTier.ReadOnly, registration.Tier);
        Assert.False(registration.RequiresApproval);
    }

    [Fact]
    public void AnnotatedWriteTool_RequiresApprovalByDefault()
    {
        var policy = CreatePolicy([Tool("write_tool", readOnly: false, destructive: false)]);

        var registration = Assert.Single(policy.Catalog);
        Assert.Equal(AgentToolTier.Write, registration.Tier);
        Assert.True(registration.RequiresApproval);
    }

    [Fact]
    public void AnnotatedWriteTool_AutoExecutesWhenKnowinglyLoosened()
    {
        var policy = CreatePolicy(
            [Tool("write_tool", readOnly: false, destructive: false)],
            requireApprovalForWrites: false);

        Assert.False(Assert.Single(policy.Catalog).RequiresApproval);
    }

    [Fact]
    public void DestructiveTool_AlwaysRequiresApproval_EvenWhenWritesAreLoosened()
    {
        var policy = CreatePolicy(
            [Tool("delete_tool", readOnly: false, destructive: true)],
            requireApprovalForWrites: false);

        var registration = Assert.Single(policy.Catalog);
        Assert.Equal(AgentToolTier.Destructive, registration.Tier);
        Assert.True(registration.RequiresApproval);
    }

    [Fact]
    public void UnannotatedTool_FailsClosedIntoApprovalTier()
    {
        // The MCP spec defaults destructiveHint to TRUE — a tool that says
        // nothing about itself is treated as dangerous, not as harmless.
        var policy = CreatePolicy([Tool("mystery_tool")], requireApprovalForWrites: false);

        var registration = Assert.Single(policy.Catalog);
        Assert.Equal(AgentToolTier.Destructive, registration.Tier);
        Assert.True(registration.RequiresApproval);
    }

    [Fact]
    public void Catalog_IsOrderedDeterministically_AcrossResolutions()
    {
        // Registration (DI enumeration) order is shuffled between the two
        // policies; the model-visible catalog must not shuffle with it.
        var first = CreatePolicy(
        [
            Tool("b_tool", readOnly: true, destructive: false),
            Tool("a_tool", readOnly: true, destructive: false),
            Tool("c_tool", readOnly: true, destructive: false),
        ]);
        var second = CreatePolicy(
        [
            Tool("c_tool", readOnly: true, destructive: false),
            Tool("b_tool", readOnly: true, destructive: false),
            Tool("a_tool", readOnly: true, destructive: false),
        ]);

        string[] expected = ["a_tool", "b_tool", "c_tool"];
        Assert.Equal(expected, first.Catalog.Select(r => r.Function.Name));
        Assert.Equal(expected, second.Catalog.Select(r => r.Function.Name));
        Assert.Equal(expected, first.Tools.Select(t => t.Name));
        Assert.Equal(first.PolicySurfaceHash, second.PolicySurfaceHash);
    }

    [Fact]
    public void UnattendedTools_ContainOnlyTheReadTier()
    {
        var policy = CreatePolicy(
        [
            Tool("read_tool", readOnly: true, destructive: false),
            Tool("write_tool", readOnly: false, destructive: false),
            Tool("delete_tool", readOnly: false, destructive: true),
        ]);

        Assert.Equal(["read_tool"], policy.UnattendedTools.Select(t => t.Name));
        Assert.True(policy.IsAllowedUnattended("read_tool"));
        Assert.False(policy.IsAllowedUnattended("write_tool"));
        Assert.False(policy.IsAllowedUnattended("delete_tool"));
        Assert.False(policy.IsAllowedUnattended("no_such_tool"));
    }

    [Fact]
    public void PolicySurfaceHash_SensitivityMatrix()
    {
        var baseline = CreatePolicy([Tool("a_tool", readOnly: true, destructive: false)]);
        var rebuilt = CreatePolicy([Tool("a_tool", readOnly: true, destructive: false)]);
        var knobFlipped = CreatePolicy(
            [Tool("a_tool", readOnly: true, destructive: false)], requireApprovalForWrites: false);
        var extraTool = CreatePolicy(
        [
            Tool("a_tool", readOnly: true, destructive: false),
            Tool("b_tool", readOnly: true, destructive: false),
        ]);
        var differentSchema = CreatePolicy([ToolWithParameter("a_tool", readOnly: true)]);
        var differentTier = CreatePolicy([Tool("a_tool", readOnly: false, destructive: false)]);

        // Same inputs → same hash (a restart must not orphan approvals)...
        Assert.Equal(baseline.PolicySurfaceHash, rebuilt.PolicySurfaceHash);
        // ...and every dimension of the surface is hash-visible.
        Assert.NotEqual(baseline.PolicySurfaceHash, knobFlipped.PolicySurfaceHash);
        Assert.NotEqual(baseline.PolicySurfaceHash, extraTool.PolicySurfaceHash);
        Assert.NotEqual(baseline.PolicySurfaceHash, differentSchema.PolicySurfaceHash);
        Assert.NotEqual(baseline.PolicySurfaceHash, differentTier.PolicySurfaceHash);
    }

    [Fact]
    public void DuplicateToolNames_FailLoudlyAtConstruction()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CreatePolicy(
        [
            Tool("dup_tool", readOnly: true, destructive: false),
            ToolWithParameter("dup_tool", readOnly: true),
        ]));
        Assert.Contains("dup_tool", exception.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------

    private static McpServerTool Tool(string name, bool? readOnly = null, bool? destructive = null) =>
        McpServerTool.Create(
            () => "\"ok\"",
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = "test tool",
                ReadOnly = readOnly,
                Destructive = destructive,
            });

    private static McpServerTool ToolWithParameter(string name, bool? readOnly = null) =>
        McpServerTool.Create(
            (string target) => $"\"{target}\"",
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = "test tool",
                ReadOnly = readOnly,
                Destructive = false,
            });

    private static AgentToolPolicy CreatePolicy(
        IEnumerable<McpServerTool> tools, bool requireApprovalForWrites = true)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var adapter = new McpToolAdapter(services, NullLoggerFactory.Instance);
        return new AgentToolPolicy(
            tools,
            adapter,
            Options.Create(new AgentOptions { RequireApprovalForWrites = requireApprovalForWrites }));
    }
}
