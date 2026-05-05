using System.Security.Cryptography;
using System.Text;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

public sealed class NormalizedTextSignatureProvider : IDuplicateSignatureProvider
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".config",
        ".cs", ".csproj", ".sln", ".ts", ".js", ".jsx", ".tsx", ".py", ".java",
        ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".ps1", ".cmd", ".bat",
        ".html", ".css", ".scss", ".sql", ".toml", ".log", ".rtf"
    };

    public DuplicateMethod Method => DuplicateMethod.NormalizedText;

    public async Task<DuplicateSignature> ComputeAsync(
        DuplicateCandidate candidate,
        CancellationToken ct = default)
    {
        if (!SupportedExtensions.Contains(candidate.File.Extension))
            throw new InvalidOperationException($"Extension {candidate.File.Extension} is not supported for normalized text review.");

        using var stream = new FileStream(
            candidate.File.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var builder = new StringBuilder((int)Math.Min(candidate.File.SizeBytes, 1_000_000));
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct) ?? string.Empty;
            builder.Append(line.TrimEnd().Normalize(NormalizationForm.FormC));
            builder.Append('\n');
        }

        var normalized = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant();

        return new DuplicateSignature
        {
            Id = 0,
            SessionId = candidate.File.SessionId,
            FileEntryId = candidate.File.Id,
            Method = Method,
            Algorithm = "TEXT-NORM-SHA256",
            SignatureText = hash,
            MetadataJson = $"{{\"normalizedBytes\":{normalized.Length}}}",
            ComputedUtc = DateTime.UtcNow,
            Status = "Ready",
        };
    }

    public static bool CanProcess(FileEntry file) => SupportedExtensions.Contains(file.Extension);
}
