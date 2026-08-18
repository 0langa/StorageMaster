using System.Globalization;

namespace StorageMaster.Storage;

internal static class UtcTimestamp
{
    public static DateTime Parse(string value) =>
        DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal)
            .UtcDateTime;
}
