namespace StorageMaster.Core.Models;

/// <summary>
/// Builds Windows command lines that round-trip correctly through the
/// MSVCRT/.NET argument parser (CommandLineToArgvW rules).
/// </summary>
public static class CommandLineArguments
{
    /// <summary>
    /// Quotes a single argument. Backslashes immediately preceding a quote
    /// (including the closing quote) are doubled so paths like <c>C:\</c>
    /// survive as <c>"C:\\"</c> instead of producing a stray literal quote.
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 &&
            !value.Contains(' ') && !value.Contains('\t') &&
            !value.Contains('"') && !value.Contains('\\'))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('"');

        var pendingBackslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (ch == '"')
            {
                // Double pending backslashes, then escape the quote itself.
                builder.Append('\\', pendingBackslashes * 2 + 1);
                pendingBackslashes = 0;
                builder.Append('"');
                continue;
            }

            builder.Append('\\', pendingBackslashes);
            pendingBackslashes = 0;
            builder.Append(ch);
        }

        // Double trailing backslashes so the closing quote is not escaped.
        builder.Append('\\', pendingBackslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    public static string Join(params string[] arguments) =>
        string.Join(' ', arguments.Select(Quote));
}
