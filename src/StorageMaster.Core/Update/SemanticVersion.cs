using System.Globalization;
using System.Text.RegularExpressions;

namespace StorageMaster.Core.Update;

internal readonly partial record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? Prerelease = null) : IComparable<SemanticVersion>
{
    private static readonly StringComparer IdentifierComparer = StringComparer.OrdinalIgnoreCase;

    public static SemanticVersion FromVersion(Version version) => new(
        version.Major,
        version.Minor,
        version.Build >= 0 ? version.Build : 0);

    public static string NormalizeTag(string tag) => tag.Trim().TrimStart('v', 'V');

    public static bool TryParseTag(string tag, out SemanticVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var match = SemverRegex().Match(NormalizeTag(tag));
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(
            major,
            minor,
            patch,
            match.Groups["prerelease"].Success ? match.Groups["prerelease"].Value : null);

        return true;
    }

    public Version ToVersion() => new(Major, Minor, Patch);

    public int CompareTo(SemanticVersion other)
    {
        var coreComparison = Major.CompareTo(other.Major);
        if (coreComparison != 0) return coreComparison;

        coreComparison = Minor.CompareTo(other.Minor);
        if (coreComparison != 0) return coreComparison;

        coreComparison = Patch.CompareTo(other.Patch);
        if (coreComparison != 0) return coreComparison;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Prerelease)
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    private static int ComparePrerelease(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return 0;

        if (string.IsNullOrWhiteSpace(left))
            return 1;

        if (string.IsNullOrWhiteSpace(right))
            return -1;

        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
        {
            var leftIsNumeric = int.TryParse(leftParts[i], out var leftNumber);
            var rightIsNumeric = int.TryParse(rightParts[i], out var rightNumber);

            int partComparison;
            if (leftIsNumeric && rightIsNumeric)
            {
                partComparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftIsNumeric != rightIsNumeric)
            {
                partComparison = leftIsNumeric ? -1 : 1;
            }
            else
            {
                partComparison = IdentifierComparer.Compare(leftParts[i], rightParts[i]);
            }

            if (partComparison != 0)
                return partComparison;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    [GeneratedRegex(
        "^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemverRegex();
}
