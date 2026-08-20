using System.Security.AccessControl;
using System.Security.Principal;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// A throwaway folder tree that fails in specific, chosen ways, so the error
/// screens can be captured without waiting for a real failure.
/// <para>
/// Error states are the hardest part of the interface to review: they are rare by
/// design, and reproducing one by hand usually means damaging something. Building
/// a tree that is genuinely unreadable — a real deny ACE, a real undecodable image
/// — makes the app take its real error paths rather than a simulated one, which is
/// the only version worth reviewing.
/// </para>
/// <para>
/// Everything lives under a single folder in TEMP that this class creates and
/// removes. It never touches anything it did not make.
/// </para>
/// </summary>
public sealed class ErrorStateFixture : IDisposable
{
    /// <summary>Marks the fixture folders so a leftover one can be recognised and cleaned up.</summary>
    private const string Prefix = "storagemaster-error-fixture-";

    public string Root { get; }

    /// <summary>The folder the scanner cannot enumerate.</summary>
    public string DeniedFolder { get; }

    /// <summary>A file with an image extension and bytes that are not an image.</summary>
    public string CorruptImage { get; }

    /// <summary>
    /// One of a pair of identical files, held open so it cannot be hashed.
    /// <para>
    /// This is the error state that is reachable without administrator rights. A
    /// normal scan skips unreadable folders silently by design, so the scan's own
    /// error list needs a deep scan; duplicate detection has to open and read each
    /// candidate, so a locked file fails there and is recorded.
    /// </para>
    /// </summary>
    public string LockedDuplicate { get; }

    private readonly SecurityIdentifier? _user;
    private FileSystemAccessRule? _denyRule;
    private FileStream? _lock;

    private ErrorStateFixture(
        string root,
        string deniedFolder,
        string corruptImage,
        string lockedDuplicate,
        SecurityIdentifier? user)
    {
        Root = root;
        DeniedFolder = deniedFolder;
        CorruptImage = corruptImage;
        LockedDuplicate = lockedDuplicate;
        _user = user;
    }

    public static ErrorStateFixture Create()
    {
        CleanUpAbandoned();

        var root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        var readable = Path.Combine(root, "readable");
        var denied = Path.Combine(root, "denied");
        var corruptFolder = Path.Combine(root, "media");

        Directory.CreateDirectory(readable);
        Directory.CreateDirectory(denied);
        Directory.CreateDirectory(corruptFolder);

        // Something to find, so the scan reports real results alongside the errors.
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(readable, $"file-{i}.txt"), new string('x', 1024 * (i + 1)));

        // Inside the denied folder before it is locked, so there is something the
        // scanner would have found had it been able to look.
        File.WriteAllText(Path.Combine(denied, "unreachable.txt"), "unreachable");

        // A JPEG header followed by noise: the extension says image, the bytes do not
        // decode. This is what a truncated download or a failing disk leaves behind.
        var corruptImage = Path.Combine(corruptFolder, "corrupt-photo.jpg");
        var bytes = new byte[4096];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        for (var i = 4; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 % 251);

        File.WriteAllBytes(corruptImage, bytes);

        // A duplicate pair, one of which will be held open. Identical content so the
        // pair is genuinely a duplicate group, large enough to clear the minimum size
        // a duplicate run applies.
        var payload = new byte[2 * 1024 * 1024];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 97);

        var readableCopy = Path.Combine(readable, "duplicate-copy.bin");
        var lockedCopy = Path.Combine(readable, "duplicate-locked.bin");
        File.WriteAllBytes(readableCopy, payload);
        File.WriteAllBytes(lockedCopy, payload);

        var fixture = new ErrorStateFixture(root, denied, corruptImage, lockedCopy, CurrentUser());
        fixture.DenyAccess();
        fixture.LockDuplicate();
        return fixture;
    }

    /// <summary>
    /// Denies this user the right to list the folder, which is what makes the
    /// scanner raise <see cref="UnauthorizedAccessException"/> and record a real
    /// scan error.
    /// <para>
    /// Only read rights are denied. Denying the right to change permissions, or to
    /// delete, would leave a folder that cannot be cleaned up — the fixture would
    /// become litter in the user's TEMP that only an administrator could remove.
    /// </para>
    /// </summary>
    private void DenyAccess()
    {
        if (_user is null)
            return;

        try
        {
            var directory = new DirectoryInfo(DeniedFolder);
            var security = directory.GetAccessControl();

            _denyRule = new FileSystemAccessRule(
                _user,
                FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny);

            security.AddAccessRule(_denyRule);
            directory.SetAccessControl(security);
        }
        catch (Exception)
        {
            // A policy that forbids editing the ACL costs this one error state, not
            // the whole run. The scan simply reports no access errors.
            _denyRule = null;
        }
    }

    /// <summary>
    /// Holds the duplicate open with no sharing, so anything that tries to read it
    /// fails. The handle is released on dispose, before the tree is deleted.
    /// </summary>
    private void LockDuplicate()
    {
        try
        {
            _lock = new FileStream(LockedDuplicate, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (Exception)
        {
            _lock = null;
        }
    }

    private void RestoreAccess()
    {
        if (_user is null || _denyRule is null)
            return;

        try
        {
            var directory = new DirectoryInfo(DeniedFolder);
            var security = directory.GetAccessControl();
            security.RemoveAccessRule(_denyRule);
            directory.SetAccessControl(security);
        }
        catch (Exception)
        {
            // Reported by the delete failing below rather than swallowed silently.
        }
        finally
        {
            _denyRule = null;
        }
    }

    private static SecurityIdentifier? CurrentUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes fixtures a previous run left behind, which happens if the process was
    /// killed between creating one and disposing it.
    /// </summary>
    private static void CleanUpAbandoned()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
            {
                var abandoned = new ErrorStateFixture(
                    directory,
                    Path.Combine(directory, "denied"),
                    string.Empty,
                    string.Empty,
                    CurrentUser());

                // The deny rule is rebuilt rather than remembered, because this
                // fixture belongs to a process that is gone.
                abandoned.ForceRemoveDenyRules();
                abandoned.DeleteTree();
            }
        }
        catch (Exception)
        {
            // Best effort. A leftover fixture is a few kilobytes in TEMP.
        }
    }

    private void ForceRemoveDenyRules()
    {
        if (_user is null || !Directory.Exists(DeniedFolder))
            return;

        try
        {
            var directory = new DirectoryInfo(DeniedFolder);
            var security = directory.GetAccessControl();
            security.RemoveAccessRuleAll(new FileSystemAccessRule(
                _user,
                FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                AccessControlType.Deny));

            directory.SetAccessControl(security);
        }
        catch (Exception)
        {
        }
    }

    private void DeleteTree()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
        RestoreAccess();
        DeleteTree();
    }
}
