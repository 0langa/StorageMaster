using System.IO.Compression;
using System.Text.Json;

namespace StorageMaster.UI.Infrastructure;

public sealed class LocalDiagnosticsService : ILocalDiagnosticsService
{
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StorageMaster",
        "logs");

    public async Task RecordAsync(string category, string message, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_logDirectory);
        var path = Path.Combine(_logDirectory, $"events-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var payload = new
        {
            utc = DateTime.UtcNow,
            category,
            message,
        };
        var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, ct);
    }

    public async Task<string> ExportBundleAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_logDirectory);
        var exportDirectory = Path.Combine(_logDirectory, "exports");
        Directory.CreateDirectory(exportDirectory);

        var zipPath = Path.Combine(exportDirectory, $"diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        var staging = Path.Combine(Path.GetTempPath(), $"sm_diag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var logFile in Directory.EnumerateFiles(_logDirectory, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                                        path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                                        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(logFile, Path.Combine(staging, Path.GetFileName(logFile)), overwrite: true);
            }

            var metadataPath = Path.Combine(staging, "environment.json");
            var metadata = new
            {
                utc = DateTime.UtcNow,
                machineName = Environment.MachineName,
                osVersion = Environment.OSVersion.ToString(),
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            };
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true,
            }), ct);

            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }
}
