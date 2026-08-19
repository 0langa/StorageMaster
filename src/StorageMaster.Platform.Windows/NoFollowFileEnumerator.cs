using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Windows no-follow file enumerator. Directory guards remain alive for the entire
/// recursive child traversal, so a queued child is always opened and classified while
/// its ancestor chain is still bound to the inspected directories.
/// </summary>
public sealed class NoFollowFileEnumerator : INoFollowFileEnumerator
{
    private readonly IFileSnapshotProvider _snapshotProvider;
    private readonly Action<string>? _beforeDirectoryGuardOpen;
    private readonly bool _forceWeakReadGuards;

    public NoFollowFileEnumerator(IFileSnapshotProvider snapshotProvider)
        : this(snapshotProvider, beforeDirectoryGuardOpen: null)
    {
    }

    /// <summary>Test seam invoked immediately before each no-follow directory open.</summary>
    internal NoFollowFileEnumerator(
        IFileSnapshotProvider snapshotProvider,
        Action<string>? beforeDirectoryGuardOpen,
        bool forceWeakReadGuards = false)
    {
        _snapshotProvider = snapshotProvider
            ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _beforeDirectoryGuardOpen = beforeDirectoryGuardOpen;
        _forceWeakReadGuards = forceWeakReadGuards;
    }

    public Task<NoFollowFileEnumerationResult> EnumerateAsync(
        string absoluteRoot,
        CancellationToken ct = default) =>
        EnumerateAsync(absoluteRoot, absoluteRoot, ct);

    public async Task<NoFollowFileEnumerationResult> EnumerateAsync(
        string absoluteBoundaryRoot,
        string absoluteScanRoot,
        CancellationToken ct = default)
    {
        var boundaryRoot = NormalizeAbsolutePath(
            absoluteBoundaryRoot,
            nameof(absoluteBoundaryRoot));
        var scanRoot = NormalizeAbsolutePath(absoluteScanRoot, nameof(absoluteScanRoot));
        if (!TryBuildDirectoryChain(boundaryRoot, scanRoot, out var directories))
        {
            throw new ArgumentException(
                "The scan root must equal or be beneath the boundary root.",
                nameof(absoluteScanRoot));
        }

        ct.ThrowIfCancellationRequested();

        // The walk is one long run of synchronous filesystem work — guard opens,
        // directory reads and snapshot captures never hit real async I/O — so hop
        // to the thread pool once, here, and stay there. A UI-thread caller would
        // otherwise be blocked for the whole analysis. (The per-file thread-pool
        // hop the snapshot provider used to force is not a substitute: it bought
        // responsiveness with a scheduling round trip for every single file.)
        return await Task.Run(
            () => EnumerateChainAsync(scanRoot, directories, ct), ct).ConfigureAwait(false);
    }

    private async Task<NoFollowFileEnumerationResult> EnumerateChainAsync(
        string scanRoot,
        IReadOnlyList<string> directories,
        CancellationToken ct)
    {
        var files = new List<FileSnapshot>();
        var errors = new List<NoFollowFileEnumerationError>();
        var guards = new List<DirectoryReadTraversalGuard>(directories.Count);
        try
        {
            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                _beforeDirectoryGuardOpen?.Invoke(directory);

                DirectoryReadTraversalGuard? guard;
                try
                {
                    guard = TryOpenReadTraversalGuard(directory);
                }
                catch (Exception ex) when (IsPathFailure(ex))
                {
                    errors.Add(CreatePathError(
                        directory,
                        ex,
                        NoFollowFileEnumerationErrorKind.InspectionFailed));
                    return CreateResult(files, errors);
                }

                if (guard is null)
                {
                    errors.Add(new NoFollowFileEnumerationError(
                        directory,
                        NoFollowFileEnumerationErrorKind.NotFound,
                        "Directory disappeared before its no-follow guard could be opened."));
                    return CreateResult(files, errors);
                }

                guards.Add(guard);
                if (guard.IsReparsePoint)
                {
                    errors.Add(new NoFollowFileEnumerationError(
                        directory,
                        NoFollowFileEnumerationErrorKind.InspectionFailed,
                        "A required boundary-to-scan-root directory is a reparse point; enumeration was refused."));
                    return CreateResult(files, errors);
                }

                AddWeakGuardWarning(directory, guard, errors);
            }

            await EnumerateGuardedDirectoryAsync(
                    scanRoot,
                    guards[^1],
                    files,
                    errors,
                    ct)
                .ConfigureAwait(false);

            return CreateResult(files, errors);
        }
        finally
        {
            DisposeGuards(guards);
        }
    }

    public async ValueTask<INoFollowFileValidationLease?> TryOpenValidatedFileAsync(
        string absoluteRoot,
        FileSnapshot expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);

        var root = NormalizeAbsolutePath(absoluteRoot, nameof(absoluteRoot));
        var filePath = NormalizeAbsolutePath(expected.Path, nameof(expected));
        ct.ThrowIfCancellationRequested();

        var parent = Path.GetDirectoryName(filePath);
        if (expected.Identity is null ||
            parent is null ||
            !TryBuildDirectoryChain(root, parent, out var directories))
            return null;

        var guards = new List<DirectoryReadTraversalGuard>(directories.Count);
        var ownershipTransferred = false;
        try
        {
            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                _beforeDirectoryGuardOpen?.Invoke(directory);

                DirectoryReadTraversalGuard? guard;
                try
                {
                    guard = TryOpenReadTraversalGuard(directory);
                }
                catch (Exception ex) when (IsPathFailure(ex))
                {
                    return null;
                }

                if (guard is null)
                    return null;

                guards.Add(guard);
                if (guard.IsReparsePoint || !guard.BlocksReplacement)
                    return null;
            }

            var capture = await CaptureRegularFileAsync(filePath, ct).ConfigureAwait(false);
            if (capture.Snapshot is not { } live ||
                capture.Failure != SnapshotCaptureFailure.None ||
                !expected.IsIdenticalTo(live))
            {
                return null;
            }

            ownershipTransferred = true;
            return new NoFollowFileValidationLease(guards, live);
        }
        finally
        {
            if (!ownershipTransferred)
                DisposeGuards(guards);
        }
    }

    private async Task EnumerateDirectoryAsync(
        string directory,
        List<FileSnapshot> files,
        List<NoFollowFileEnumerationError> errors,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _beforeDirectoryGuardOpen?.Invoke(directory);

        DirectoryReadTraversalGuard? guard;
        try
        {
            guard = TryOpenReadTraversalGuard(directory);
        }
        catch (Exception ex) when (IsPathFailure(ex))
        {
            errors.Add(CreatePathError(
                directory,
                ex,
                NoFollowFileEnumerationErrorKind.InspectionFailed));
            return;
        }

        if (guard is null)
        {
            errors.Add(new NoFollowFileEnumerationError(
                directory,
                NoFollowFileEnumerationErrorKind.NotFound,
                "Directory disappeared before its no-follow guard could be opened."));
            return;
        }

        using (guard)
        {
            if (!guard.IsReparsePoint)
                AddWeakGuardWarning(directory, guard, errors);

            await EnumerateGuardedDirectoryAsync(directory, guard, files, errors, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task EnumerateGuardedDirectoryAsync(
        string directory,
        DirectoryReadTraversalGuard guard,
        List<FileSnapshot> files,
        List<NoFollowFileEnumerationError> errors,
        CancellationToken ct)
    {
        if (guard.IsReparsePoint)
            return;

        // Names and attributes both come out of the enumeration buffer. The former
        // File.GetAttributes per entry was a second filesystem call for data the
        // directory read already returned — over a browser cache or Temp tree that
        // is one extra syscall per file before any work begins.
        List<ChildEntry> entries;
        try
        {
            entries = [];
            foreach (var child in new FileSystemEnumerable<ChildEntry>(
                         directory,
                         static (ref FileSystemEntry entry) =>
                             new ChildEntry(entry.ToFullPath(), entry.Attributes),
                         ChildEnumerationOptions))
            {
                entries.Add(child);
            }
        }
        catch (Exception ex) when (IsPathFailure(ex))
        {
            errors.Add(CreatePathError(
                directory,
                ex,
                NoFollowFileEnumerationErrorKind.EnumerationFailed));
            return;
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            // This check is only an early skip/type hint. A child directory or file is
            // opened no-follow again immediately before use, closing the swap window.
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                // Intentionally await while the caller's guard scope remains active. Every
                // ancestor guard therefore stays held throughout child traversal.
                await EnumerateDirectoryAsync(entry.FullPath, files, errors, ct).ConfigureAwait(false);
                continue;
            }

            var capture = await CaptureRegularFileAsync(entry.FullPath, ct).ConfigureAwait(false);
            if (capture.Failure == SnapshotCaptureFailure.ReparsePoint)
                continue;

            if (capture.Snapshot is { } snapshot)
            {
                files.Add(snapshot);
                continue;
            }

            errors.Add(new NoFollowFileEnumerationError(
                entry.FullPath,
                ToPublicErrorKind(capture.Failure),
                capture.Message));
        }
    }

    /// <summary>One directory child, with the attributes the enumeration already read.</summary>
    private readonly record struct ChildEntry(string FullPath, FileAttributes Attributes);

    // Mirrors the options Directory.GetFileSystemEntries(path) uses, so the set of
    // entries considered is unchanged: nothing skipped by attribute (hidden and
    // system caches must stay visible) and access failures still surface.
    private static readonly EnumerationOptions ChildEnumerationOptions = new()
    {
        MatchType = MatchType.Win32,
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Holds a no-follow file handle with no delete/write sharing while the normal snapshot
    /// provider opens the same bound path. This prevents a regular file from being replaced
    /// by a file symlink or other reparse point between classification and snapshot capture.
    /// </summary>
    private async ValueTask<SnapshotCapture> CaptureRegularFileAsync(
        string path,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        FileEntryGuard? guard;
        try
        {
            guard = TryOpenFileNoFollow(path);
        }
        catch (Exception ex) when (IsPathFailure(ex))
        {
            return new SnapshotCapture(
                null,
                ToCaptureFailure(ex, SnapshotCaptureFailure.InspectionFailed),
                ex.Message);
        }

        if (guard is null)
        {
            return new SnapshotCapture(
                null,
                SnapshotCaptureFailure.NotFound,
                "File disappeared before its no-follow guard could be opened.");
        }

        using (guard)
        {
            if (guard.IsReparsePoint)
                return new SnapshotCapture(null, SnapshotCaptureFailure.ReparsePoint, string.Empty);

            if (guard.IsDirectory)
            {
                return new SnapshotCapture(
                    null,
                    SnapshotCaptureFailure.InspectionFailed,
                    "Path changed from a regular file to a directory before snapshot capture.");
            }

            FileSnapshot? snapshot;
            try
            {
                snapshot = await _snapshotProvider
                    .TakeSnapshotAsync(path, ct)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableSnapshotFailure(ex))
            {
                return new SnapshotCapture(
                    null,
                    SnapshotCaptureFailure.SnapshotUnavailable,
                    ex.Message);
            }

            if (snapshot is null)
            {
                return new SnapshotCapture(
                    null,
                    SnapshotCaptureFailure.SnapshotUnavailable,
                    "The file snapshot provider could not capture the guarded file.");
            }

            if (!string.Equals(snapshot.Path, path, StringComparison.OrdinalIgnoreCase) ||
                (snapshot.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return new SnapshotCapture(
                    null,
                    SnapshotCaptureFailure.InspectionFailed,
                    "The captured snapshot did not describe the guarded regular file.");
            }

            if (snapshot.Identity is null)
            {
                return new SnapshotCapture(
                    null,
                    SnapshotCaptureFailure.IdentityUnavailable,
                    "A stable filesystem identity was unavailable for the guarded file.");
            }

            return new SnapshotCapture(snapshot, SnapshotCaptureFailure.None, string.Empty);
        }
    }

    private static FileEntryGuard? TryOpenFileNoFollow(string path)
    {
        var handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return null;

            throw CreateIOException("Unable to lock file for safe inspection", path, error);
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var info,
                (uint)Marshal.SizeOf<FileAttributeTagInfoBuffer>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw CreateIOException("Unable to inspect the locked file", path, error);
        }

        return new FileEntryGuard(handle, info.FileAttributes);
    }

    private static bool TryBuildDirectoryChain(
        string root,
        string descendantDirectory,
        out IReadOnlyList<string> directories)
    {
        directories = Array.Empty<string>();
        var relative = Path.GetRelativePath(root, descendantDirectory);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        var result = new List<string> { root };
        if (!relative.Equals(".", StringComparison.Ordinal))
        {
            var current = root;
            foreach (var part in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or "..")
                    return false;

                current = Path.Combine(current, part);
                result.Add(current);
            }
        }

        directories = result;
        return true;
    }

    private static NoFollowFileEnumerationResult CreateResult(
        List<FileSnapshot> files,
        List<NoFollowFileEnumerationError> errors) =>
        new(files.ToArray(), errors.ToArray());

    private DirectoryReadTraversalGuard? TryOpenReadTraversalGuard(string path) =>
        DirectoryTraversalInterop.TryOpenNoFollowForReadTraversal(
            path,
            attemptStrongGuard: !_forceWeakReadGuards);

    private static void AddWeakGuardWarning(
        string path,
        DirectoryReadTraversalGuard guard,
        List<NoFollowFileEnumerationError> errors)
    {
        if (guard.BlocksReplacement)
            return;

        errors.Add(new NoFollowFileEnumerationError(
            path,
            NoFollowFileEnumerationErrorKind.ReplacementGuardUnavailable,
            "Read-only analysis continued with a weak no-follow guard that does not block " +
            "directory replacement; these results cannot authorize later path-based deletion."));
    }

    private static string NormalizeAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Path must be fully qualified.", parameterName);

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or SecurityException)
        {
            throw new ArgumentException("Path is not a valid absolute path.", parameterName, ex);
        }
    }

    private static NoFollowFileEnumerationError CreatePathError(
        string path,
        Exception exception,
        NoFollowFileEnumerationErrorKind fallbackKind) =>
        new(path, ToPublicErrorKind(exception, fallbackKind), exception.Message);

    private static NoFollowFileEnumerationErrorKind ToPublicErrorKind(
        Exception exception,
        NoFollowFileEnumerationErrorKind fallbackKind)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException)
            return NoFollowFileEnumerationErrorKind.NotFound;

        if (exception is UnauthorizedAccessException or SecurityException ||
            exception.InnerException is Win32Exception { NativeErrorCode: ErrorAccessDenied })
        {
            return NoFollowFileEnumerationErrorKind.AccessDenied;
        }

        return fallbackKind;
    }

    private static NoFollowFileEnumerationErrorKind ToPublicErrorKind(
        SnapshotCaptureFailure failure) => failure switch
        {
            SnapshotCaptureFailure.NotFound => NoFollowFileEnumerationErrorKind.NotFound,
            SnapshotCaptureFailure.AccessDenied => NoFollowFileEnumerationErrorKind.AccessDenied,
            SnapshotCaptureFailure.SnapshotUnavailable => NoFollowFileEnumerationErrorKind.SnapshotUnavailable,
            SnapshotCaptureFailure.IdentityUnavailable => NoFollowFileEnumerationErrorKind.IdentityUnavailable,
            _ => NoFollowFileEnumerationErrorKind.InspectionFailed,
        };

    private static SnapshotCaptureFailure ToCaptureFailure(
        Exception exception,
        SnapshotCaptureFailure fallback)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException)
            return SnapshotCaptureFailure.NotFound;

        if (exception is UnauthorizedAccessException or SecurityException ||
            exception.InnerException is Win32Exception { NativeErrorCode: ErrorAccessDenied })
        {
            return SnapshotCaptureFailure.AccessDenied;
        }

        return fallback;
    }

    private static bool IsPathFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private static bool IsRecoverableSnapshotFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or Win32Exception
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException;

    private static IOException CreateIOException(string operation, string path, int error) =>
        new($"{operation}: {path}", new Win32Exception(error));

    private static void DisposeGuards(IReadOnlyList<DirectoryReadTraversalGuard> guards)
    {
        for (var index = guards.Count - 1; index >= 0; index--)
            guards[index].Dispose();
    }

    private sealed class NoFollowFileValidationLease(
        List<DirectoryReadTraversalGuard> guards,
        FileSnapshot liveSnapshot) : INoFollowFileValidationLease
    {
        private List<DirectoryReadTraversalGuard>? _guards = guards;

        public FileSnapshot LiveSnapshot { get; } = liveSnapshot;

        public void Dispose()
        {
            var ownedGuards = Interlocked.Exchange(ref _guards, null);
            if (ownedGuards is not null)
                DisposeGuards(ownedGuards);
        }
    }

    private sealed class FileEntryGuard(SafeFileHandle handle, uint attributes) : IDisposable
    {
        public bool IsDirectory { get; } = (attributes & FileAttributeDirectory) != 0;
        public bool IsReparsePoint { get; } = (attributes & FileAttributeReparsePoint) != 0;

        public void Dispose() => handle.Dispose();
    }

    private readonly record struct SnapshotCapture(
        FileSnapshot? Snapshot,
        SnapshotCaptureFailure Failure,
        string Message);

    private enum SnapshotCaptureFailure
    {
        None,
        ReparsePoint,
        NotFound,
        AccessDenied,
        InspectionFailed,
        SnapshotUnavailable,
        IdentityUnavailable,
    }

    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfoBuffer
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfoBuffer fileInformation,
        uint bufferSize);
}
