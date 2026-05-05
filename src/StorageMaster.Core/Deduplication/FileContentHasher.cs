using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using StorageMaster.Core.Interfaces;

namespace StorageMaster.Core.Deduplication;

public sealed class FileContentHasher : IFileContentHasher
{
    private const int BufferSize = 1024 * 128;
    private const int SampleSize = 1024 * 64;

    public async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            useAsync: true);

        using var sha = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);

            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<string> ComputePartialHashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            useAsync: true);

        var length = stream.Length;
        var sample = ArrayPool<byte>.Shared.Rent(SampleSize);
        try
        {
            using var sha = SHA256.Create();
            await HashSegmentAsync(stream, sha, sample, 0, ct);

            if (length > SampleSize * 2)
                await HashSegmentAsync(stream, sha, sample, Math.Max(0, (length / 2) - (SampleSize / 2)), ct);

            if (length > SampleSize)
                await HashSegmentAsync(stream, sha, sample, Math.Max(0, length - SampleSize), ct);

            var sizeBytes = Encoding.UTF8.GetBytes(length.ToString());
            sha.TransformBlock(sizeBytes, 0, sizeBytes.Length, null, 0);
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sample);
        }
    }

    private static async Task HashSegmentAsync(
        FileStream stream,
        HashAlgorithm hash,
        byte[] buffer,
        long offset,
        CancellationToken ct)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var read = await stream.ReadAsync(buffer.AsMemory(0, SampleSize), ct);
        if (read > 0)
            hash.TransformBlock(buffer, 0, read, null, 0);
    }
}
