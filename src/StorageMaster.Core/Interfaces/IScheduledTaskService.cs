using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IScheduledTaskService
{
    Task<IReadOnlyList<ScheduledTaskInfo>> ListAsync(CancellationToken ct = default);
    Task<ScheduledTaskInfo> UpsertAsync(ScheduledJobDefinition job, CancellationToken ct = default);
    Task DeleteAsync(string jobId, CancellationToken ct = default);
    Task UpdateRunOutcomeAsync(string jobId, string status, string message, CancellationToken ct = default);
    Task<ScheduledJobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default);
}
