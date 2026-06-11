using VueApp1.Server.Services;
using Xunit;

namespace VueApp1.Server.UnitTests.Services;

public class PublicUriOptionsValidatorTests
{
    [Fact]
    public void Validate_PassesCommittedDefault_AndRejectsNonAbsoluteBaseUri()
    {
        var validator = new PublicUriOptionsValidator();

        // The committed default must stay boot-valid (zero-secrets boot).
        Assert.True(validator.Validate(name: null, new PublicUriOptions()).Succeeded);

        // A scheme-less value is the classic misconfiguration: without
        // boot-time validation it only explodes when the first absolute
        // link is generated.
        var result = validator.Validate(
            name: null,
            new PublicUriOptions { BaseUri = "app.example.com" });

        Assert.True(result.Failed);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("PublicUri:BaseUri", result.FailureMessage, StringComparison.Ordinal);
    }
}
