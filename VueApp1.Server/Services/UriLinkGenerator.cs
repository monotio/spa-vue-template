using Microsoft.Extensions.Options;

namespace VueApp1.Server.Services;

public sealed class PublicUriOptions
{
    public const string SectionName = "PublicUri";

    /// <summary>Public base URI of this app, e.g. <c>https://app.example.com</c>.</summary>
    public string BaseUri { get; set; } = "https://localhost:7191";
}

/// <summary>
/// Boot-time validation for <see cref="PublicUriOptions"/> (ValidateOnStart in
/// Program.cs). Without it, an unset or relative BaseUri surfaces only when
/// the first absolute link is generated — <see cref="UriLinkGenerator"/>
/// throws lazily in its constructor.
/// </summary>
public sealed class PublicUriOptionsValidator : IValidateOptions<PublicUriOptions>
{
    public ValidateOptionsResult Validate(string? name, PublicUriOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUri))
        {
            return ValidateOptionsResult.Fail(
                "PublicUri:BaseUri must be set to an absolute http(s) URI, e.g. https://app.example.com.");
        }

        if (!Uri.TryCreate(options.BaseUri, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                $"PublicUri:BaseUri must be an absolute http(s) URI; got '{options.BaseUri}'.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Generates absolute URIs from the configured public base URI instead of the
/// incoming Host header. Links that leave the request context (emails,
/// notifications, QR codes) must never be derived from request headers — the
/// Host header is attacker-controlled (host-header injection).
/// </summary>
public interface IUriLinkGenerator
{
    /// <summary>Absolute URI for a relative path, e.g. <c>ToAbsolute("/weather")</c>.</summary>
    Uri ToAbsolute(string relativePath);

    /// <summary>
    /// Absolute URI for a controller action, e.g.
    /// <c>ForAction(nameof(WeatherForecastController.Get), "WeatherForecast")</c>.
    /// </summary>
    Uri ForAction(string actionName, string controllerName, object? routeValues = null);
}

public sealed class UriLinkGenerator(
    LinkGenerator linkGenerator,
    IOptions<PublicUriOptions> options) : IUriLinkGenerator
{
    private readonly Uri _baseUri = new(options.Value.BaseUri, UriKind.Absolute);

    public Uri ToAbsolute(string relativePath) => new(_baseUri, relativePath);

    public Uri ForAction(string actionName, string controllerName, object? routeValues = null)
    {
        var path = linkGenerator.GetPathByAction(actionName, controllerName, routeValues)
            ?? throw new InvalidOperationException(
                $"No route found for {controllerName}.{actionName}.");
        return new Uri(_baseUri, path);
    }
}
