using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface ISettingsSnapshotProvider
{
    AppSettings Current { get; }
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Atomically load-mutate-save. Callers that change a subset of settings
    /// must use this instead of LoadAsync + SaveAsync so concurrent writers
    /// (settings page, low-disk monitor, scheduler) cannot drop each other's
    /// changes. The default implementation is the naive non-atomic sequence
    /// for lightweight test fakes; real repositories serialize mutations.
    /// </summary>
    async Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var settings = await LoadAsync(ct);
        mutate(settings);
        await SaveAsync(settings, ct);
        return settings;
    }
}
