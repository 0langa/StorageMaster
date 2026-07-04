using FluentAssertions;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// Regression tests for Win32 command-line quoting. The elevated deep-scan
/// worker previously passed <c>--path "C:\"</c>, which the MSVCRT parser reads
/// as a literal quote (argument becomes <c>C:"</c>) — breaking deep scans of
/// drive roots.
/// </summary>
public sealed class CommandLineArgumentsTests
{
    [Theory]
    [InlineData(@"C:\", "\"C:\\\\\"")]                          // trailing backslash doubled
    [InlineData(@"C:\Users\Test", "\"C:\\Users\\Test\"")]       // interior backslashes untouched
    [InlineData(@"C:\Program Files\", "\"C:\\Program Files\\\\\"")]
    public void Quote_PathsWithBackslashes_ProduceParseableQuoting(string input, string expected)
        => CommandLineArguments.Quote(input).Should().Be(expected);

    [Fact]
    public void Quote_SimpleToken_IsNotQuoted()
        => CommandLineArguments.Quote("--deep").Should().Be("--deep");

    [Fact]
    public void Quote_EmbeddedQuote_IsEscaped()
        => CommandLineArguments.Quote("say \"hi\"").Should().Be("\"say \\\"hi\\\"\"");

    [Fact]
    public void Quote_BackslashesBeforeQuote_AreDoubledAndQuoteEscaped()
        => CommandLineArguments.Quote(@"a\""b").Should().Be("\"a\\\\\\\"b\"");

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files\StorageMaster\")]
    [InlineData(@"C:\weird ""name""\dir\")]
    [InlineData(@"trailing\\")]
    public void Quote_RoundTripsThroughWindowsArgumentParsing(string value)
    {
        var quoted = CommandLineArguments.Quote(value);
        ParseWindowsCommandLine(quoted).Should().ContainSingle().Which.Should().Be(value);
    }

    [Fact]
    public void Join_ScanInvocation_RoundTripsEveryArgument()
    {
        var joined = CommandLineArguments.Join("--cli", "scan", "--deep", "--path", @"C:\");
        ParseWindowsCommandLine(joined).Should().Equal("--cli", "scan", "--deep", "--path", @"C:\");
    }

    /// <summary>
    /// Reference implementation of the MSVCRT/CommandLineToArgvW argument rules
    /// (2n backslashes + quote → n backslashes, toggle in-quotes; 2n+1 → n
    /// backslashes + literal quote).
    /// </summary>
    private static List<string> ParseWindowsCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;

        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { backslashes++; i++; }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                    hasToken = true;
                }
                else
                {
                    current.Append('\\', backslashes);
                    hasToken = true;
                }
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                i++;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                i++;
                continue;
            }

            current.Append(c);
            hasToken = true;
            i++;
        }

        if (hasToken)
            args.Add(current.ToString());
        return args;
    }
}
