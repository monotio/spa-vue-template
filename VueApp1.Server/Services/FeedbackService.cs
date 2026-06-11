namespace VueApp1.Server.Services;

public interface IFeedbackService
{
    Task<ServiceResponse<FeedbackReceipt>> SubmitAsync(
        FeedbackRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deliberately storeless (the template ships no database — docs/DATA.md):
/// the receipt is the teaching payload, and its server-minted id is what
/// makes the Idempotency-Key replay on FeedbackController observable.
/// The whitespace rule demonstrates the 400-vs-422 split: malformed input
/// fails DataAnnotations binding (400), well-formed input failing a domain
/// rule returns 422 through the ServiceResponse pipeline.
/// </summary>
public class FeedbackService(TimeProvider timeProvider) : IFeedbackService
{
    public Task<ServiceResponse<FeedbackReceipt>> SubmitAsync(
        FeedbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var message = request.Message.Trim();
        if (message.Length == 0)
        {
            // The 422 domain-rule path: [StringLength] counts whitespace and
            // the implicit non-nullable required check only rejects null, so
            // well-formed-but-blank input reaches the service.
            return Task.FromResult(ServiceResponse<FeedbackReceipt>.UnprocessableEntity(
                "Feedback message must contain non-whitespace characters."));
        }

        var receipt = new FeedbackReceipt(Guid.NewGuid(), timeProvider.GetUtcNow(), message);
        return Task.FromResult(ServiceResponse<FeedbackReceipt>.Success(receipt));
    }
}
