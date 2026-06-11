using Microsoft.Extensions.Primitives;

namespace VueApp1.Server.Mcp;

/// <summary>
/// Origin-header validation (DNS-rebinding / CSRF defense-in-depth) for
/// endpoints that execute side effects without auth — here the MCP endpoint;
/// reuse it on token-authenticated POST endpoints too (docs/PATTERNS.md).
/// Browsers always send <c>Origin</c> on cross-origin requests, and
/// DNS-rebinding attacks reach localhost-bound services with an
/// attacker-controlled Origin. The allowlist derives from the existing
/// <c>AllowedHosts</c> configuration (zero per-environment upkeep; <c>*</c>
/// disables the check); requests WITHOUT an Origin header pass — non-browser
/// clients (MCP CLIs, agents) don't send one.
/// </summary>
public sealed class ValidateOriginFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var origin = context.HttpContext.Request.Headers.Origin;
        if (StringValues.IsNullOrEmpty(origin)
            || IsOriginAllowed(origin.ToString(), configuration["AllowedHosts"]))
        {
            return await next(context);
        }

        // ProblemDetails like every other error surface in the template; 403
        // (not 400) so probes and logs distinguish "denied" from "malformed".
        return TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Origin not allowed.");
    }

    /// <summary>
    /// Subdomain wildcards use host-filtering semantics: <c>*.example.com</c>
    /// (or the leading-dot form <c>.example.com</c>) matches
    /// <c>api.example.com</c> but NOT the apex <c>example.com</c> — list the
    /// apex separately. Comparison is host-only (any scheme/port), matching
    /// how <c>AllowedHosts</c> itself is evaluated.
    /// </summary>
    public static bool IsOriginAllowed(string origin, string? allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(origin);

        // Missing/blank AllowedHosts means host filtering is off — mirror that.
        if (string.IsNullOrWhiteSpace(allowedHosts))
        {
            return true;
        }

        // An Origin that isn't an absolute URI (including the literal "null"
        // an opaque browsing context sends) never matches a restrictive list.
        var originHost = Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            ? originUri.Host
            : null;

        foreach (var entry in allowedHosts.Split(
            ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry == "*")
            {
                return true;
            }

            if (originHost is null)
            {
                continue;
            }

            if (entry.StartsWith("*.", StringComparison.Ordinal) || entry.StartsWith('.'))
            {
                var suffix = entry.TrimStart('*');
                if (originHost.Length > suffix.Length
                    && originHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(originHost, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
