using Microsoft.Windows.ApplicationModel.Resources;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Resolves localized strings for code paths that cannot use <c>x:Uid</c>.
/// <para>
/// XAML should use <c>x:Uid</c> wherever possible — it is resolved by the
/// framework, survives theme and template changes, and is checked by the build.
/// This exists for the cases <c>x:Uid</c> cannot reach: strings composed in view
/// models, values that vary at runtime, and formatted messages.
/// </para>
/// <para>
/// It is deliberately NOT used for log lines, CLI output or exception text.
/// Those stay English by policy — see docs/public/LOCALIZATION.md — because they
/// are read by whoever is debugging rather than by the user, and stable output
/// matters more than locale. <c>LocalizationScopeTests</c> enforces that.
/// </para>
/// </summary>
public static class Loc
{
    // The Windows App SDK loader, not the older Windows.ApplicationModel.Resources
    // one: this app is unpackaged, and only this type resolves against the
    // generated resources.pri without a package identity.
    private static readonly ResourceLoader Loader = new();

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>.
    /// <para>
    /// A missing key returns the key itself rather than an empty string. An
    /// obviously wrong label on screen is far easier to notice and fix than a
    /// silently blank one, and the automated parity tests catch it before release
    /// anyway.
    /// </para>
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        try
        {
            var value = Loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            // Resource loading must never take a page down over a label.
            return key;
        }
    }

    /// <summary>
    /// Localized string with positional arguments.
    /// <para>
    /// Formatting uses the invariant culture on purpose. Numbers and sizes are
    /// formatted by their own converters, which already apply the user's culture;
    /// running the composed string through the current culture as well would
    /// double-format them.
    /// </para>
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);

        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            // A placeholder mismatch between languages must not crash the UI. The
            // parity tests exist to stop this reaching a release; if one slips
            // through, showing the untouched template is the least-bad outcome.
            return template;
        }
    }
}
