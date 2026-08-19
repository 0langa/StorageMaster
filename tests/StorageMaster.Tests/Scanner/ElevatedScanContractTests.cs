using FluentAssertions;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Scanner;

/// <summary>
/// The contract between the unelevated UI and the elevated scan worker.
/// <para>
/// This is the one place in the app where a defect is a privilege defect, so the
/// pieces that decide what gets elevated and what comes back are asserted rather
/// than trusted: the progress channel's parsing, and the argument quoting that
/// decides what an elevated process is actually told to do.
/// </para>
/// </summary>
public sealed class ElevatedScanContractTests
{
    [Fact]
    public void AProgressLineSurvivesARoundTrip()
    {
        var report = new ElevatedScanProgressReport
        {
            FilesScanned = 1_482_023,
            FoldersScanned = 252_443,
            BytesScanned = 166_400_000_000,
            ErrorCount = 599,
            CurrentPath = @"C:\Windows\System32",
            IsComplete = false,
        };

        var parsed = ElevatedScanProgressReport.TryParse(report.ToJsonLine());

        parsed.Should().NotBeNull();
        parsed!.FilesScanned.Should().Be(report.FilesScanned);
        parsed.BytesScanned.Should().Be(report.BytesScanned);
        parsed.CurrentPath.Should().Be(report.CurrentPath);
        parsed.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ATerminalLineCarriesTheSessionAndStatus()
    {
        var report = new ElevatedScanProgressReport
        {
            IsComplete = true,
            SessionId = 42,
            Status = nameof(ScanStatus.Completed),
        };

        var parsed = ElevatedScanProgressReport.TryParse(report.ToJsonLine())!;

        parsed.IsComplete.Should().BeTrue();
        parsed.SessionId.Should().Be(42);
        parsed.Status.Should().Be("Completed");
        parsed.Error.Should().BeNull();
    }

    /// <summary>
    /// The reader tails a file a different process is appending to, so it will see
    /// partial lines. A torn line must be ignored, not throw and not be mistaken for
    /// a finished scan.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"FilesScanned\":123")]
    [InlineData("not json at all")]
    [InlineData("{}{")]
    public void AnUnreadableLineIsIgnoredRatherThanThrowing(string line)
    {
        var act = () => ElevatedScanProgressReport.TryParse(line);

        act.Should().NotThrow();
        ElevatedScanProgressReport.TryParse(line)?.IsComplete.Should().NotBe(true);
    }

    /// <summary>
    /// Paths reach an elevated process as command-line arguments. A path that loses
    /// or gains a quote there is a path the elevated process misreads — this exact
    /// class of bug once corrupted deep scans of <c>C:\</c>.
    /// </summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"D:\Users\Someone\My Documents\")]
    [InlineData(@"C:\path with ""quote""")]
    public void APathSurvivesQuotingForAnElevatedProcess(string path)
    {
        var quoted = CommandLineArguments.Quote(path);
        var parsed = SplitAsWindowsWould(quoted);

        parsed.Should().ContainSingle("a single path must stay a single argument")
            .Which.Should().Be(path);
    }

    [Fact]
    public void TheWorkerCommandLineKeepsItsArgumentsSeparate()
    {
        var line = CommandLineArguments.Join(
            "--headless", "scan",
            "--path", @"C:\Program Files",
            "--deep",
            "--progress", @"C:\Users\A B\AppData\Local\Temp\p.jsonl");

        SplitAsWindowsWould(line).Should().Equal(
            "--headless", "scan",
            "--path", @"C:\Program Files",
            "--deep",
            "--progress", @"C:\Users\A B\AppData\Local\Temp\p.jsonl");
    }

    /// <summary>
    /// Splits a command line the way the Windows CRT does, so the assertions above
    /// test what the elevated process will actually receive rather than what the
    /// quoting helper believes it produced.
    /// </summary>
    private static List<string> SplitAsWindowsWould(string commandLine)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var backslashes = 0;

        void FlushBackslashes(bool beforeQuote)
        {
            // A run of backslashes is literal unless it precedes a quote, in which
            // case each pair collapses to one.
            var count = beforeQuote ? backslashes / 2 : backslashes;
            current.Append('\\', count);
            backslashes = 0;
        }

        foreach (var c in commandLine)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    break;

                case '"':
                    var escaped = backslashes % 2 == 1;
                    FlushBackslashes(beforeQuote: true);
                    if (escaped)
                        current.Append('"');
                    else
                        inQuotes = !inQuotes;
                    break;

                case ' ' when !inQuotes:
                    FlushBackslashes(beforeQuote: false);
                    if (current.Length > 0)
                    {
                        arguments.Add(current.ToString());
                        current.Clear();
                    }

                    break;

                default:
                    FlushBackslashes(beforeQuote: false);
                    current.Append(c);
                    break;
            }
        }

        FlushBackslashes(beforeQuote: false);
        if (current.Length > 0)
            arguments.Add(current.ToString());

        return arguments;
    }
}
