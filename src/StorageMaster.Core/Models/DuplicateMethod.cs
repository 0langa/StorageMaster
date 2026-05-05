namespace StorageMaster.Core.Models;

public enum DuplicateMethod
{
    ExactSha256,
    NormalizedText,
    ImagePHash,
    VideoPHash,
    AudioFingerprint,
}
