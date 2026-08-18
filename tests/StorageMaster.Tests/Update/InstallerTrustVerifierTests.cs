using FluentAssertions;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.Update;

public sealed class InstallerTrustVerifierTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"StorageMaster-unsigned-{Guid.NewGuid():N}.exe");

    [Fact]
    public async Task VerifyAsync_UnsignedFile_ReturnsNotSignedWithoutTrust()
    {
        await File.WriteAllTextAsync(_path, "not an executable");
        var verifier = new InstallerTrustVerifier();

        var result = await verifier.VerifyAsync(_path);

        result.IsSigned.Should().BeFalse();
        result.IsSignatureValid.Should().BeFalse();
        result.HasTrustedTimestamp.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_PreCancelledToken_PropagatesCancellation()
    {
        await File.WriteAllTextAsync(_path, "not an executable");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var verifier = new InstallerTrustVerifier();

        Func<Task> act = () => verifier.VerifyAsync(_path, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
