using Microsoft.Extensions.DependencyInjection;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Honours the opt-in "always run as administrator" setting at startup.
/// <para>
/// Checked before any window exists, because the point is to replace this process
/// rather than to elevate one halfway through starting. A user who has not opted
/// in is never prompted: the default route for a deep scan is a short-lived
/// elevated worker, which keeps the long-running window unprivileged.
/// </para>
/// </summary>
public static class ElevationRelaunch
{
    /// <summary>
    /// Marks a process that is already the product of a relaunch.
    /// <para>
    /// Without it a failure to actually gain rights — a policy that silently denies
    /// elevation, say — would relaunch forever, each copy seeing the same setting
    /// and trying again. The marker makes the second attempt impossible rather
    /// than merely unlikely.
    /// </para>
    /// </summary>
    public const string AlreadyRelaunchedFlag = "--elevated-relaunch";

    /// <summary>
    /// Returns true when an elevated copy was started and this process should exit
    /// without showing a window.
    /// <para>
    /// Returns false for every other outcome, including the user declining the
    /// prompt. A declined prompt must leave a working unelevated app rather than
    /// nothing at all — the setting is a preference, not a requirement.
    /// </para>
    /// </summary>
    public static bool TryRelaunchElevated(IServiceProvider services, string[] args)
    {
        if (args.Any(a => a.Equals(AlreadyRelaunchedFlag, StringComparison.OrdinalIgnoreCase)))
            return false;

        var admin = services.GetRequiredService<IAdminService>();
        if (admin.IsRunningAsAdmin)
            return false;

        try
        {
            // Blocked deliberately: nothing may render before this decision, and the
            // settings read is a single small row. Task.Run keeps the repository's
            // continuations off a thread this call is about to block.
            var settings = Task.Run(() =>
                    services.GetRequiredService<ISettingsRepository>().LoadAsync())
                .GetAwaiter()
                .GetResult();

            if (!settings.AlwaysRunAsAdministrator)
                return false;

            var forwarded = args
                .Where(a => !a.Equals(AlreadyRelaunchedFlag, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var arguments = CommandLineArguments.Join([.. forwarded, AlreadyRelaunchedFlag]);

            return admin.TryStartElevated(arguments);
        }
        catch (Exception)
        {
            // An unreadable settings file must not stop the app from starting. The
            // unelevated app still works; only the convenience is lost.
            return false;
        }
    }
}
