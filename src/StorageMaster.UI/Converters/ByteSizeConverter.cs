using Microsoft.UI.Xaml.Data;

namespace StorageMaster.UI.Converters;

/// <summary>Converts a long byte count into a human-readable string (e.g. "1.4 GB").</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is long bytes)  return Format(bytes);
        if (value is int  intVal) return Format(intVal);
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    public static string Format(long bytes) =>
        bytes switch
        {
            >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
            >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
            _           => $"{bytes} B",
        };
}
