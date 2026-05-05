using Microsoft.UI.Xaml.Data;

namespace StorageMaster.UI.Converters;

/// <summary>
/// Converts a bool to a Segoe MDL2 Assets chevron glyph:
///   true  → "" (ChevronUp  / E972) — group is expanded
///   false → "" (ChevronDown / E971) — group is collapsed
/// </summary>
public sealed class BoolToChevronConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
