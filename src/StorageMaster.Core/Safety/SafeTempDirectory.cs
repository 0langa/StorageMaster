namespace StorageMaster.Core.Safety;

public static class SafeTempDirectory
{
    public static string Create(string namePrefix)
    {
        if (string.IsNullOrWhiteSpace(namePrefix) ||
            namePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            namePrefix.Contains(Path.DirectorySeparatorChar) ||
            namePrefix.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Temporary directory prefix must be a safe file name segment.", nameof(namePrefix));
        }

        var path = Path.Combine(Path.GetTempPath(), $"{namePrefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static bool TryDelete(string path, string requiredNamePrefix, Action<Exception>? onFailure = null)
    {
        try
        {
            if (!IsDirectTempChild(path, requiredNamePrefix))
                throw new InvalidOperationException($"Refusing recursive delete outside guarded temp path: {path}");

            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            onFailure?.Invoke(ex);
            return false;
        }
    }

    public static bool IsDirectTempChild(string path, string requiredNamePrefix)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(requiredNamePrefix))
            return false;

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(fullPath)?.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(fullPath);

        return string.Equals(parent, tempRoot, StringComparison.OrdinalIgnoreCase) &&
               name.StartsWith(requiredNamePrefix + "_", StringComparison.Ordinal);
    }
}
