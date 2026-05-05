namespace StorageMaster.Core.Models;

public sealed record InstallerTrustVerificationResult
{
    public required bool IsSigned { get; init; }
    public required bool IsSignatureValid { get; init; }
    public required bool HasTrustedTimestamp { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
