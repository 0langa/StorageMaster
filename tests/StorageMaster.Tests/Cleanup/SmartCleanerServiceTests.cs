using FluentAssertions;
using StorageMaster.Core.SmartCleaner;

namespace StorageMaster.Tests.Cleanup;

public sealed class SmartCleanerServiceTests
{
    [Fact]
    public void ScanDirectory_CancelledToken_PropagatesCancellation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smclean_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "item.tmp"), "data");
        long bytes = 0;
        var paths = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            var act = () => SmartCleanerService.ScanDirectory(dir, ref bytes, paths, cts.Token);
            act.Should().Throw<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
