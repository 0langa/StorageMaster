using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;

namespace StorageMaster.UI.Infrastructure;

public sealed class DesktopNotificationService(
    ILocalDiagnosticsService diagnostics,
    ILogger<DesktopNotificationService> logger) : INotificationService
{
    public event EventHandler<DesktopNotificationEventArgs>? NotificationRaised;

    public Task ShowInfoAsync(string title, string message, CancellationToken ct = default) =>
        RaiseAsync("info", title, message, ct);

    public Task ShowWarningAsync(string title, string message, CancellationToken ct = default) =>
        RaiseAsync("warning", title, message, ct);

    public Task ShowErrorAsync(string title, string message, CancellationToken ct = default) =>
        RaiseAsync("error", title, message, ct);

    private async Task RaiseAsync(string level, string title, string message, CancellationToken ct)
    {
        logger.LogInformation("[notification:{Level}] {Title}: {Message}", level, title, message);
        await diagnostics.RecordAsync("notification", $"{level}|{title}|{message}", ct);
        NotificationRaised?.Invoke(this, new DesktopNotificationEventArgs(level, title, message));
    }
}

public sealed record DesktopNotificationEventArgs(string Level, string Title, string Message);
