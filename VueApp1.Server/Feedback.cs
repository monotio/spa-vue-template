using System.ComponentModel.DataAnnotations;

namespace VueApp1.Server;

/// <summary>
/// Sample create-style payload for the idempotent POST teaching endpoint
/// (FeedbackController). DataAnnotations produce the automatic 400
/// ValidationProblemDetails; domain rules live in FeedbackService (422).
/// </summary>
// A non-positional record on purpose: the OpenAPI schema exporter reads
// validation attributes from PROPERTIES, so a positional record's
// parameter-targeted [StringLength] validates at runtime but silently
// vanishes from the contract (clients would hit undocumented 400s) — and
// MVC rejects [property:]-attributed positional records at runtime, so
// init properties are the only shape where the 3..2000 rule is both
// enforced AND published (docs/API.md). No explicit [Required]: `required`
// plus the non-nullable type already make a missing Message a binding 400,
// while whitespace-only input deliberately passes annotations so the domain
// rule in FeedbackService can demonstrate the 422 path.
public record FeedbackRequest
{
    [StringLength(2000, MinimumLength = 3)]
    public required string Message { get; init; }
}

/// <summary>
/// Server acknowledgement. <see cref="Id"/> is minted server-side, which
/// makes idempotent replay observable: a retry that re-executed would mint a
/// NEW id; a replayed response carries the SAME one.
/// </summary>
public record FeedbackReceipt(Guid Id, DateTimeOffset ReceivedAt, string Message);
