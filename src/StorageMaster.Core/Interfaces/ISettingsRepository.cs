using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
