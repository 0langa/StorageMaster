namespace StorageMaster.Core.Models;

/// <summary>
/// Human-readable byte formatting shared by cleanup rules and CLI output.
/// Floating-point division — 1.9 GB must not display as "1.0 GB".
/// </summary>
public static class ByteFormat
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}
