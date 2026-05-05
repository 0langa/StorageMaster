namespace StorageMaster.Core.Interfaces;

public interface INotificationService
{
    Task ShowInfoAsync(string title, string message, CancellationToken ct = default);
    Task ShowWarningAsync(string title, string message, CancellationToken ct = default);
    Task ShowErrorAsync(string title, string message, CancellationToken ct = default);
}
