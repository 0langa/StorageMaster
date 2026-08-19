using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;
using StorageMaster.Core.Theming;
using StorageMaster.UI.Pages;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Captures the reviewable states that are not simply a page sitting idle:
/// confirmation dialogs, accent variants, and a scan in flight.
/// <para>
/// These are the scenarios docs/public/VISUAL_REGRESSION.md asks for and the plain
/// page capture cannot reach. A dialog exists only while someone is deleting
/// something; an accent is only interesting on a page that colours state with it;
/// progress exists only while work runs. Reaching them by hand means a person
/// clicking through the app in each language, which is exactly the manual step the
/// harness exists to remove.
/// </para>
/// </summary>
public static class ScenarioCapture
{
    public static async Task RunAsync(
        MainWindow window,
        ScreenCaptureOptions options,
        string language,
        string theme,
        string size)
    {
        foreach (var scenario in options.Scenarios)
        {
            try
            {
                switch (scenario.ToLowerInvariant())
                {
                    case "dialogs":
                        await CaptureDialogsAsync(window, options, language, theme, size);
                        break;

                    case "accents":
                        await CaptureAccentsAsync(window, options, language, size);
                        break;

                    case "progress":
                        await CaptureProgressAsync(window, options, language, theme, size);
                        break;

                    default:
                        Console.WriteLine($"unknown scenario '{scenario}'");
                        break;
                }
            }
            catch (Exception ex)
            {
                // One scenario that cannot be reached must not cost the others.
                Console.WriteLine($"scenario {scenario} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Rebuilds each safety confirmation from the resource keys the real call sites
    /// use and renders it.
    /// <para>
    /// The dialog is rendered rather than the window: a <c>ContentDialog</c> lives in
    /// the popup layer, which is a sibling of the window content, so rendering the
    /// window would produce the page with no dialog on it — a capture that looks
    /// fine and shows nothing of what was asked for.
    /// </para>
    /// </summary>
    private static async Task CaptureDialogsAsync(
        MainWindow window,
        ScreenCaptureOptions options,
        string language,
        string theme,
        string size)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null)
        {
            Console.WriteLine("scenario dialogs failed: no window root");
            return;
        }

        foreach (var scenario in ScenarioCatalogue.SafetyDialogs)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = Loc.Get(scenario.TitleKey),
                Content = Loc.Format(scenario.BodyKey, scenario.BodyArguments),
                PrimaryButtonText = Loc.Get(scenario.PrimaryKey),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };

            // Not awaited: ShowAsync completes when the dialog closes, and the dialog
            // has to be on screen to be rendered.
            var showing = dialog.ShowAsync();

            try
            {
                await Task.Delay(options.SettleMilliseconds);

                var path = Path.Combine(
                    options.OutputDirectory,
                    $"{scenario.Id}--{theme}--{language}{size}.png");

                await ScreenCaptureHarness.CaptureAsync(dialog, path);
                Console.WriteLine($"captured {scenario.Id} -> {Path.GetFileName(path)}");
            }
            finally
            {
                dialog.Hide();
                await showing;
            }
        }
    }

    /// <summary>
    /// Captures the pages that colour state with the accent, once per accent.
    /// <para>
    /// The theme is swapped in place here rather than per process, which is the
    /// opposite of how language is handled — deliberately. Accents are applied by
    /// recolouring existing brushes, so swapping one is exactly what the running app
    /// does when a user picks a different accent, and capturing it that way also
    /// proves the live swap repaints.
    /// </para>
    /// </summary>
    private static async Task CaptureAccentsAsync(
        MainWindow window,
        ScreenCaptureOptions options,
        string language,
        string size)
    {
        var themeService = App.Services.GetRequiredService<ThemeService>();
        var preference = options.Theme?.Equals("light", StringComparison.OrdinalIgnoreCase) == true
            ? ThemePreference.Light
            : ThemePreference.Dark;

        // Whatever accent the run started with, restored at the end. Without this the
        // last accent applied here leaked into every scenario that ran afterwards —
        // a progress capture came out violet and looked like a deliberate choice.
        var originalAccent = ThemeCatalog.DefaultAccentId;

        // Pages whose severity and gauge colours come from the accent ramp.
        var pages = new[] { typeof(DashboardPage), typeof(DriveHealthPage) };

        foreach (var accent in ThemeCatalog.Accents)
        {
            themeService.Apply(preference, accent.Id);

            foreach (var pageType in pages)
            {
                if (!await ScreenCaptureHarness.NavigateAndWaitAsync(window, pageType))
                    continue;

                await ScreenCaptureHarness.SettleAsync(window, options.SettleMilliseconds);

                var tag = NavigationRoutes.PageToTag[pageType];
                var path = Path.Combine(
                    options.OutputDirectory,
                    $"{tag}--accent-{accent.Id}--{language}{size}.png");

                await ScreenCaptureHarness.CaptureAsync(window.CaptureRoot, path);
                Console.WriteLine($"captured {tag} accent {accent.Id} -> {Path.GetFileName(path)}");
            }
        }

        themeService.Apply(preference, originalAccent);
    }

    /// <summary>
    /// Starts a real scan and captures it mid-flight.
    /// <para>
    /// Scans the demo pack by default, never a real user folder: a capture run must
    /// not be the thing that reads someone's whole disk. The scan is a read-only
    /// operation, so this scenario cannot delete anything even if pointed elsewhere.
    /// </para>
    /// </summary>
    private static async Task CaptureProgressAsync(
        MainWindow window,
        ScreenCaptureOptions options,
        string language,
        string theme,
        string size)
    {
        var scanPath = options.ScanPath ?? DefaultScanPath();

        if (!Directory.Exists(scanPath))
        {
            Console.WriteLine($"scenario progress skipped: {scanPath} does not exist (pass --scan-path)");
            return;
        }

        if (!await ScreenCaptureHarness.NavigateAndWaitAsync(window, typeof(ScanPage)))
            return;

        var scan = App.Services.GetRequiredService<ScanViewModel>();

        // The page kicks off InitializeAsync from OnNavigatedTo without awaiting it,
        // and StartScanAsync refuses to run while that is in flight. Skipping this
        // wait meant the command returned immediately and the run still wrote a
        // "Scan-complete" capture — of a page that had never scanned anything.
        if (!await WaitUntilAsync(() => !scan.IsInitializing))
        {
            Console.WriteLine("scenario progress skipped: the scan page did not finish initializing");
            return;
        }

        scan.SelectedPath = scanPath;

        if (!await WaitUntilAsync(() => scan.CanStartScan))
        {
            Console.WriteLine($"scenario progress skipped: cannot scan {scanPath} ({scan.ScanPathError})");
            return;
        }

        var running = scan.StartScanCommand.ExecuteAsync(null);

        // Wait for the scan to actually be running, rather than guessing a delay. A
        // fixed wait was wrong in the worst way: a small fixture finished inside it,
        // so the file named "running" held a completed page and looked plausible.
        if (!await WaitUntilAsync(() => scan.IsScanning))
        {
            Console.WriteLine("scenario progress skipped: the scan never started");
            await running;
            return;
        }

        // Let the counters reach something worth reading, but never wait so long that
        // a quick scan finishes first.
        await WaitUntilAsync(() => !scan.IsScanning || scan.FilesScanned > 0, TimeSpan.FromSeconds(3));

        if (scan.IsScanning)
        {
            var path = Path.Combine(
                options.OutputDirectory,
                $"Scan-running--{theme}--{language}{size}.png");

            await ScreenCaptureHarness.CaptureAsync(window.CaptureRoot, path);
            Console.WriteLine($"captured Scan-running -> {Path.GetFileName(path)}");
        }
        else
        {
            // Said plainly rather than written as a file: a fixture that finishes too
            // fast has no running state to review, and a silently missing capture is
            // indistinguishable from one nobody looked at.
            //
            // The file count is part of the message because the usual cause is not
            // speed at all — it is that the target was excluded. Scanning C:\Windows
            // finishes instantly with nothing, because skipping system folders is on
            // by default, and "point at a larger folder" would send someone looking
            // in the wrong place entirely.
            Console.WriteLine(
                $"scenario progress: the scan finished before a running state could be captured "
                + $"({scan.FilesScanned:N0} file(s) scanned). If that count is zero the path is "
                + "excluded by the scan scope — system folders are skipped by default. "
                + "Otherwise point --scan-path at a larger folder.");
        }

        await running;

        await ScreenCaptureHarness.SettleAsync(window, options.SettleMilliseconds);
        var donePath = Path.Combine(
            options.OutputDirectory,
            $"Scan-complete--{theme}--{language}{size}.png");

        await ScreenCaptureHarness.CaptureAsync(window.CaptureRoot, donePath);
        Console.WriteLine($"captured Scan-complete -> {Path.GetFileName(donePath)}");
    }

    /// <summary>
    /// Polls a view-model condition on the UI thread. Returns false on timeout so a
    /// caller can report why a scenario was skipped instead of capturing whatever
    /// state happened to be on screen.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(15);
        var waited = TimeSpan.Zero;
        var poll = TimeSpan.FromMilliseconds(50);

        while (waited < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(poll);
            waited += poll;
        }

        return condition();
    }

    private static string DefaultScanPath()
        => Path.Combine(AppContext.BaseDirectory, "demo");
}
