namespace StorageMaster.Core.Interfaces;

public interface IFileContentHasher
{
    Task<string> ComputeSha256Async(string path, CancellationToken ct = default);
    Task<string> ComputePartialHashAsync(string path, CancellationToken ct = default);
}
