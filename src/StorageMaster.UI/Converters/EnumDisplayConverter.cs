using System.Globalization;
using Microsoft.UI.Xaml.Data;
using StorageMaster.Core.Localization;

namespace StorageMaster.UI.Converters;

/// <summary>
/// Renders an enum value as the text a user should read.
/// <para>
/// The settings drop-downs bind straight to <c>Enum.GetValues</c>, so without this
/// they show the identifier — "CleanupExecuteSafe", "ShortestPath" — in every
/// language, English included. The value is looked up as
/// <c>Enum_&lt;TypeName&gt;_&lt;ValueName&gt;</c>, which keeps the keys derivable
/// rather than hand-mapped: adding an enum member and its key is enough, and
/// <c>LocalizationScopeTests</c> is unable to catch a missing one, so
/// <c>EnumDisplayTests</c> asserts that every member of every bound enum resolves.
/// </para>
/// </summary>
public sealed partial class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not Enum enumValue)
            return value?.ToString() ?? string.Empty;

        // Weekday names belong to the culture, not to this app. Taking them from
        // .NET means they match the rest of Windows and stay correct for languages
        // the app itself does not ship.
        if (value is DayOfWeek day)
            return ActiveCulture.DateTimeFormat.GetDayName(day);

        return Loc.Get(KeyFor(enumValue));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException(
            "Display text is one-way. The drop-downs bind SelectedItem to the enum value itself.");

    /// <summary>The resource key for an enum value.</summary>
    public static string KeyFor(Enum value)
        => $"Enum_{value.GetType().Name}_{value}";

    /// <summary>
    /// The culture matching the language the app is actually showing, which is not
    /// necessarily the OS culture — the user can pin a language in Settings.
    /// </summary>
    private static CultureInfo ActiveCulture
    {
        get
        {
            try
            {
                return CultureInfo.GetCultureInfo(LocalizationCatalog.ActiveLanguage);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.CurrentUICulture;
            }
        }
    }
}
