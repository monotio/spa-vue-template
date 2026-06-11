using System.Globalization;

namespace VueApp1.Server.UnitTests.Infrastructure;

/// <summary>
/// Temporarily switches <see cref="CultureInfo.CurrentCulture"/> and
/// <see cref="CultureInfo.CurrentUICulture"/> for the lifetime of the
/// instance: <c>using var _ = new CultureSwitcher("tr-TR");</c>.
/// The suite already runs under a deliberately hostile non-invariant default
/// (xunit.runner.json <c>"culture"</c>) — reach for this only when a test
/// needs a SPECIFIC different culture, never to escape the pinned one.
/// See docs/TESTING.md, "Determinism choices".
/// </summary>
internal sealed class CultureSwitcher : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

    public CultureSwitcher(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
    }
}
