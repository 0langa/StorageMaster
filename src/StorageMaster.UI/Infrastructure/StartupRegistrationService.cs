using Microsoft.Win32;

namespace StorageMaster.UI.Infrastructure;

public sealed class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StorageMaster";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the user startup registry key.");

        if (!enabled)
        {
            if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable path is unavailable.");
        key.SetValue(ValueName, $"\"{exePath}\" --start-in-tray", RegistryValueKind.String);
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value &&
               value.Contains("--start-in-tray", StringComparison.OrdinalIgnoreCase);
    }
}
