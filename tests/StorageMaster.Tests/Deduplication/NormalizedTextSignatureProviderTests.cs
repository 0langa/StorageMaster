using FluentAssertions;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Deduplication;

public sealed class NormalizedTextSignatureProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sm_norm_{Guid.NewGuid():N}");

    public NormalizedTextSignatureProviderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ComputeAsync_NormalizesLineEndingsWhitespaceAndUnicode()
    {
        var provider = new NormalizedTextSignatureProvider();
        var fileA = Path.Combine(_tempDir, "a.txt");
        var fileB = Path.Combine(_tempDir, "b.txt");

        await File.WriteAllTextAsync(fileA, "hello  \r\nworld\t \r\ncaf\u00E9\r\n");
        await File.WriteAllTextAsync(fileB, "hello\nworld\ncafe\u0301\n");

        var sigA = await provider.ComputeAsync(new DuplicateCandidate(MakeFileEntry(1, fileA)));
        var sigB = await provider.ComputeAsync(new DuplicateCandidate(MakeFileEntry(2, fileB)));

        sigA.Method.Should().Be(DuplicateMethod.NormalizedText);
        sigA.Algorithm.Should().Be("TEXT-NORM-SHA256");
        sigA.SignatureText.Should().Be(sigB.SignatureText, "equivalent text should hash identically after normalization");
        sigA.MetadataJson.Should().Contain("normalizedBytes");
    }

    [Fact]
    public async Task ComputeAsync_UnsupportedExtension_Throws()
    {
        var provider = new NormalizedTextSignatureProvider();
        var path = Path.Combine(_tempDir, "photo.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        var act = () => provider.ComputeAsync(new DuplicateCandidate(MakeFileEntry(3, path)));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static FileEntry MakeFileEntry(long id, string path) => new()
    {
        Id = id,
        SessionId = 42,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = new FileInfo(path).Length,
        CreatedUtc = DateTime.UtcNow,
        ModifiedUtc = DateTime.UtcNow,
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Document,
    };
}
