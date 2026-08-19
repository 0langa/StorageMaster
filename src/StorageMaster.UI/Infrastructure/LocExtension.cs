using Microsoft.UI.Xaml.Markup;
using StorageMaster.Core.Localization;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Resolves a catalogue string in XAML: <c>Text="{i18n:Loc Key=Nav_Dashboard}"</c>.
/// <para>
/// This takes the place of <c>x:Uid</c>, which cannot be used here — <c>x:Uid</c>
/// is resolved by MRT, and MRT's resource compiler is unavailable to this build
/// (see <see cref="LocalizationCatalog"/>). The practical difference is that a
/// markup extension sets one property rather than every property sharing a uid,
/// so a control needing both a header and a tooltip carries two keys.
/// </para>
/// <para>
/// The value is resolved once, when the page is parsed. Changing language
/// therefore takes effect on pages loaded afterwards, which is why Settings asks
/// for a restart — the same restart the built-in WinUI control text needs anyway.
/// </para>
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>The resource key. An unknown key renders as itself.</summary>
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Loc.Get(Key);
}
