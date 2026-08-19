using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageMaster.Core.Models;

/// <summary>
/// One line of the progress channel between an elevated scan worker and the
/// unelevated UI that started it.
/// <para>
/// A deep scan needs administrator rights, and the UI deliberately does not run
/// elevated. The work therefore happens in a short-lived elevated child process,
/// which reports back by appending JSON lines to a file the UI tails. That keeps
/// the scan visible in the app instead of behind a console window the user cannot
/// follow, and it keeps the elevated surface as small as a single scan.
/// </para>
/// <para>
/// A file rather than a pipe: the child is the higher-integrity process, so it can
/// write where the UI can read, and a file survives the child exiting before the
/// UI has read the last line. The channel is one-way — the UI never sends anything
/// to the elevated process, which is what keeps this from being an escalation path.
/// </para>
/// </summary>
public sealed record ElevatedScanProgressReport
{
    /// <summary>Progress so far, or the final counts when <see cref="IsComplete"/>.</summary>
    public long FilesScanned { get; init; }

    public long FoldersScanned { get; init; }

    public long BytesScanned { get; init; }

    public int ErrorCount { get; init; }

    public string CurrentPath { get; init; } = string.Empty;

    public bool IsComplete { get; init; }

    /// <summary>Set on the terminal line only: the session the worker recorded.</summary>
    public long? SessionId { get; init; }

    /// <summary>Set on the terminal line only: Completed, Cancelled or Failed.</summary>
    public string? Status { get; init; }

    /// <summary>Set on the terminal line only when the worker could not finish.</summary>
    public string? Error { get; init; }

    private static readonly JsonSerializerOptions Format = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, Format);

    /// <summary>
    /// Parses one line, returning null for anything unreadable.
    /// <para>
    /// The reader tails a file another process is appending to, so it can see a
    /// half-written final line. A torn line is not an error — the next read gets
    /// the whole thing.
    /// </para>
    /// </summary>
    public static ElevatedScanProgressReport? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ElevatedScanProgressReport>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
