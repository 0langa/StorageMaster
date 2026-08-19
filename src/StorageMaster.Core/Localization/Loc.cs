namespace StorageMaster.Core.Localization;

/// <summary>
/// Short facade over <see cref="LocalizationCatalog"/> for call sites that read
/// better without the longer name — view models, converters, and the XAML markup
/// extension.
/// <para>
/// It is deliberately NOT used for log lines, CLI output or exception text. Those
/// stay English by policy — see docs/public/LOCALIZATION.md — because they are
/// read by whoever is debugging rather than by the user, and stable output matters
/// more than locale. <c>LocalizationScopeTests</c> enforces that.
/// </para>
/// </summary>
public static class Loc
{
    /// <inheritdoc cref="LocalizationCatalog.Get"/>
    public static string Get(string key) => LocalizationCatalog.Get(key);

    /// <inheritdoc cref="LocalizationCatalog.Format"/>
    public static string Format(string key, params object?[] args)
        => LocalizationCatalog.Format(key, args);
}
