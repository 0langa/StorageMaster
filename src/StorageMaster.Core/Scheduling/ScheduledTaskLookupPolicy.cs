namespace StorageMaster.Core.Scheduling;

/// <summary>
/// Distinguishes a genuinely absent scheduled task from a failed targeted query.
/// A successful all-task inventory is required before a nonzero targeted query can
/// be treated as not found.
/// </summary>
public static class ScheduledTaskLookupPolicy
{
    public static ScheduledTaskLookupStatus Evaluate(
        int targetedQueryExitCode,
        int inventoryExitCode,
        string inventoryCsv,
        string taskName)
    {
        if (targetedQueryExitCode == 0)
            return ScheduledTaskLookupStatus.Found;

        if (inventoryExitCode != 0)
            return ScheduledTaskLookupStatus.QueryFailed;

        return ContainsTask(inventoryCsv, taskName)
            ? ScheduledTaskLookupStatus.QueryFailed
            : ScheduledTaskLookupStatus.NotFound;
    }

    internal static bool ContainsTask(string inventoryCsv, string taskName)
    {
        var expected = NormalizeTaskName(taskName);
        foreach (var line in inventoryCsv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryReadFirstCsvField(line, out var listedName) &&
                string.Equals(NormalizeTaskName(listedName), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeTaskName(string taskName) =>
        taskName.Trim().TrimStart('\\');

    private static bool TryReadFirstCsvField(string line, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (line[0] != '"')
        {
            var comma = line.IndexOf(',');
            value = (comma < 0 ? line : line[..comma]).Trim();
            return value.Length > 0;
        }

        var builder = new System.Text.StringBuilder();
        for (var index = 1; index < line.Length; index++)
        {
            var character = line[index];
            if (character != '"')
            {
                builder.Append(character);
                continue;
            }

            if (index + 1 < line.Length && line[index + 1] == '"')
            {
                builder.Append('"');
                index++;
                continue;
            }

            value = builder.ToString();
            return value.Length > 0;
        }

        return false;
    }
}

public enum ScheduledTaskLookupStatus
{
    Found,
    NotFound,
    QueryFailed,
}
