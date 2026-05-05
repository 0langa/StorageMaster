namespace StorageMaster.Core.Models;

public sealed record DuplicateCandidate(
    FileEntry File,
    FileIdentity? Identity = null);
