using System.ComponentModel.DataAnnotations;

namespace VueApp1.Server;

/// <summary>
/// Sample create-style payload for the idempotent POST teaching endpoint
/// (FeedbackController). DataAnnotations produce the automatic 400
/// ValidationProblemDetails; domain rules live in FeedbackService (422).
/// </summary>
// Parameter-targeted attributes, NOT [property:]: MVC associates validation
// metadata for positional records with the primary-constructor parameter and
// rejects property-attributed records at runtime. No explicit [Required]:
// the non-nullable type already makes a missing Message a binding 400, while
// whitespace-only input deliberately passes annotations so the domain rule
// in FeedbackService can demonstrate the 422 path.
public record FeedbackRequest(
    [StringLength(2000, MinimumLength = 3)]
    string Message);

/// <summary>
/// Server acknowledgement. <see cref="Id"/> is minted server-side, which
/// makes idempotent replay observable: a retry that re-executed would mint a
/// NEW id; a replayed response carries the SAME one.
/// </summary>
public record FeedbackReceipt(Guid Id, DateTimeOffset ReceivedAt, string Message);
