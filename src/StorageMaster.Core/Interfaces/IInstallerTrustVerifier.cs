using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IInstallerTrustVerifier
{
    Task<InstallerTrustVerificationResult> VerifyAsync(
        string installerPath,
        CancellationToken ct = default);
}
