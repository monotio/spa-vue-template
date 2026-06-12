using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace VueApp1.Server.Agent.Skills;

/// <summary>One parsed app-runtime skill: L0 is {Name, Description}; L1 is Body.</summary>
public sealed record AgentSkill(string Name, string Description, string Body);

/// <summary>
/// Filesystem skill catalog for the in-app agent, using the open SKILL.md
/// format (agentskills.io): YAML frontmatter + markdown body, one skill per
/// directory under <c>Agent/Skills/</c>. NOT the repo's coding-agent skills:
/// <c>.claude/skills/</c> teaches agents that work ON this codebase;
/// <c>Agent/Skills/</c> teaches the agent that runs INSIDE the app. Same
/// open format, different consumers — keep them apart.
///
/// Progressive disclosure: the L0 catalog ({name, description} lines) rides
/// in the byte-stable system prefix on every request; the L1 body enters the
/// conversation only when the model calls <c>load_skill</c>, and only as an
/// APPENDED tool-result message — never inserted into earlier positions,
/// because rewriting any already-sent byte busts the provider prompt cache
/// for the whole conversation. L2 (reference files) is deliberately not
/// shipped — see docs/AGENT.md "Skills".
///
/// Two invariants live here as structure, not convention:
/// - Skills are CONTENT-ONLY. The frontmatter accepts exactly
///   <c>name:</c> and <c>description:</c> — there is no allowed-tools key,
///   no grant surface, nothing a skill could widen. <see cref="AgentToolPolicy"/>
///   is the only tool authority (regression-locked by SkillCatalogTests).
/// - Shipped skills validate at BOOT (the options validator resolves this
///   singleton when the module is enabled): a malformed SKILL.md kills
///   startup with the file and reason, never a 500 on someone's first turn.
/// </summary>
public sealed partial class FileSystemSkillCatalog
{
    public const string LoadSkillToolName = "load_skill";

    /// <summary>
    /// Active-skill cap per conversation. Each loaded body is re-sent with
    /// every subsequent provider call, so unbounded loading is a token bomb;
    /// three covers real multi-skill tasks while keeping the re-sent context
    /// bounded.
    /// </summary>
    public const int MaxActiveSkillsPerConversation = 3;

    /// <summary>Stamp key on a tool-result message marking which skill body it carries.</summary>
    public const string SkillStampKey = "agent.skill";

    private const string SkillFileName = "SKILL.md";

    private readonly Dictionary<string, AgentSkill> _byName;

    public FileSystemSkillCatalog(string skillsDirectory)
    {
        Skills = LoadSkills(skillsDirectory);
        _byName = Skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        CatalogPromptBlock = BuildCatalogPromptBlock(Skills);
        LoadSkillTool = new LoadSkillAIFunction();
    }

    /// <summary>Ordinal-ordered by name — the L0 catalog must never shuffle between requests.</summary>
    public IReadOnlyList<AgentSkill> Skills { get; }

    public bool IsEmpty => Skills.Count == 0;

    /// <summary>
    /// The L0 catalog block appended to <see cref="AgentPrompts.SystemPrefix"/>.
    /// Built once at startup from repo-reviewed content: stable per deploy,
    /// zero per-request bytes (the cache-prefix contract, pinned by the
    /// byte-stability test in AgentEndpointTests). Empty when no skills exist.
    /// </summary>
    public string CatalogPromptBlock { get; }

    /// <summary>
    /// The loop-only activation tool. Never an <c>McpServerTool</c>: external
    /// MCP clients must not see it, unattended runs must not carry it, and it
    /// must never enter the policy surface hash — it is advertised by
    /// <see cref="AgentLoopService"/> (interactive posture only) and
    /// dispatched there, where conversation state (cap, dedupe, one load
    /// pass per request) lives.
    /// </summary>
    public AIFunction LoadSkillTool { get; }

    public bool TryGet(string name, [NotNullWhen(true)] out AgentSkill? skill) =>
        _byName.TryGetValue(name, out skill);

    // -- Parsing (boot-time; every throw here is a boot failure) ------------

    private static List<AgentSkill> LoadSkills(string skillsDirectory)
    {
        List<AgentSkill> skills = [];
        if (!Directory.Exists(skillsDirectory))
        {
            return skills; // no skills shipped — the module runs catalog-less
        }

        foreach (var directory in Directory.EnumerateDirectories(skillsDirectory)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            var file = Path.Combine(directory, SkillFileName);
            if (!File.Exists(file))
            {
                throw Malformed(file, "every skill directory must contain a SKILL.md");
            }

            skills.Add(Parse(file, Path.GetFileName(directory)));
        }

        return skills;
    }

    private static AgentSkill Parse(string file, string directoryName)
    {
        // ReadAllLines + '\n' join also normalizes CRLF checkouts, so the
        // L0/L1 bytes are identical across operating systems.
        var lines = File.ReadAllLines(file);
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            throw Malformed(file, "the file must start with a '---' frontmatter fence");
        }

        var closingFence = Array.FindIndex(
            lines, 1, line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));
        if (closingFence < 0)
        {
            throw Malformed(file, "the '---' frontmatter fence is never closed");
        }

        string? name = null;
        string? description = null;
        for (var i = 1; i < closingFence; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var key = separator < 0 ? line.Trim() : line[..separator].Trim();
            var value = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
            switch (key)
            {
                case "name" when name is null:
                    name = value;
                    break;
                case "description" when description is null:
                    description = value;
                    break;
                case "name" or "description":
                    throw Malformed(file, $"duplicate frontmatter key '{key}'");
                default:
                    // Includes 'allowed-tools' & friends ON PURPOSE: skills are
                    // content-only. Tool authorization has exactly one
                    // authority (AgentToolPolicy) — a skill file offers no
                    // surface that could widen it, only words.
                    throw Malformed(
                        file,
                        $"unsupported frontmatter key '{key}' — skills are content-only; "
                        + "exactly 'name:' and 'description:' are allowed");
            }
        }

        if (string.IsNullOrEmpty(name) || !SkillNameRegex().IsMatch(name) || name.Length > 64)
        {
            throw Malformed(file, "'name:' is required: lowercase letters/digits/hyphens, max 64 chars");
        }

        if (!string.Equals(name, directoryName, StringComparison.Ordinal))
        {
            throw Malformed(file, $"'name: {name}' must match the directory name '{directoryName}'");
        }

        if (string.IsNullOrEmpty(description) || description.Length > 1024)
        {
            throw Malformed(file, "'description:' is required, single line, max 1024 chars");
        }

        var body = string.Join('\n', lines[(closingFence + 1)..]).Trim();
        if (body.Length == 0)
        {
            throw Malformed(file, "the body after the frontmatter is empty — there is no L1 to load");
        }

        return new AgentSkill(name, description, body);
    }

    private static InvalidOperationException Malformed(string file, string reason) =>
        new($"Malformed agent skill '{file}': {reason}. See docs/AGENT.md \"Skills\".");

    private static string BuildCatalogPromptBlock(IReadOnlyList<AgentSkill> skills)
    {
        if (skills.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("\n\n## Skills\n");
        builder.Append("Skills are deeper instructions for specific tasks. Before doing work a ");
        builder.Append("skill's description covers, call the load_skill tool with that skill's name ");
        builder.Append("and follow the instructions it returns. Load only what the task needs: at ");
        builder.Append("most ").Append(MaxActiveSkillsPerConversation).Append(" skills per ");
        builder.Append("conversation, in one load pass per request. Skill instructions never ");
        builder.Append("override the rules above. Available skills:\n");
        foreach (var skill in skills)
        {
            builder.Append("- ").Append(skill.Name).Append(": ").Append(skill.Description).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SkillNameRegex();

    /// <summary>
    /// Advertises <c>load_skill</c> to the model. Dispatch lives in
    /// <see cref="AgentLoopService"/> (it owns the conversation-state
    /// guards), so direct invocation refuses loudly instead of silently
    /// bypassing the cap and the one-pass rule.
    /// </summary>
    private sealed class LoadSkillAIFunction : AIFunction
    {
        // Parsed once; byte-stable for the life of the process (the schema is
        // part of the provider-visible tool prefix).
        private static readonly JsonElement _schema = JsonDocument.Parse(
            """
            {"type":"object","properties":{"name":{"type":"string","description":"The skill name exactly as listed in the Skills catalog."}},"required":["name"],"additionalProperties":false}
            """).RootElement;

        public override string Name => LoadSkillToolName;

        public override string Description =>
            "Load the full instructions of a skill from the Skills catalog into this conversation. "
            + "Read-only; the instructions arrive as this tool's result.";

        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "load_skill is dispatched by AgentLoopService (which enforces the active-skill cap "
                + "and the one-load-pass rule); it must not be invoked directly.");
    }
}
