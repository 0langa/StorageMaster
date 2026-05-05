using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IFileIdentityProvider
{
    Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken ct = default);
}
