namespace StorageMaster.UI.Infrastructure;

public interface ILocalDiagnosticsService
{
    Task RecordAsync(string category, string message, CancellationToken ct = default);
    Task<string> ExportBundleAsync(CancellationToken ct = default);
}
