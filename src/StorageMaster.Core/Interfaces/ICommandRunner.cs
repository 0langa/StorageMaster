namespace StorageMaster.Core.Interfaces;

public interface ICommandRunner
{
    Task<int> RunAsync(
        string[] args,
        bool headless,
        TextWriter output,
        TextWriter error,
        CancellationToken ct = default);
}
