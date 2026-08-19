using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Imaging;
using StorageMaster.Core.Localization;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Options for <see cref="ScreenCaptureHarness"/>, parsed from the command line.
/// </summary>
public sealed class ScreenCaptureOptions
{
    public required string OutputDirectory { get; init; }

    /// <summary>Language tag to capture in. Null keeps whatever the settings say.</summary>
    public string? Language { get; init; }

    /// <summary>Theme name to capture in. Null keeps whatever the settings say.</summary>
    public string? Theme { get; init; }

    /// <summary>Capture only these route tags. Empty means every page.</summary>
    public IReadOnlyList<string> Pages { get; init; } = [];

    /// <summary>
    /// How long to let a page settle before capturing it. Pages load their data
    /// asynchronously, and a capture taken too early shows an empty state rather
    /// than the screen being reviewed.
    /// </summary>
    public int SettleMilliseconds { get; init; } = 2000;

    /// <summary>
    /// Parses <c>--capture-screens &lt;dir&gt; [--language x] [--theme x] [--pages a,b]
    /// [--settle ms]</c>. Returns null when the first argument is not the capture flag.
    /// </summary>
    public static ScreenCaptureOptions? TryParse(string[] args)
    {
        if (args.Length < 2 || !args[0].Equals("--capture-screens", StringComparison.OrdinalIgnoreCase))
            return null;

        string? Value(string name)
        {
            var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var settle = Value("--settle");

        return new ScreenCaptureOptions
        {
            OutputDirectory = args[1],
            Language = Value("--language"),
            Theme = Value("--theme"),
            Pages = Value("--pages")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            SettleMilliseconds = int.TryParse(settle, out var parsed) ? parsed : 2000,
        };
    }
}

/// <summary>
/// Renders each page to a PNG so the interface can be reviewed without anyone
/// owning the screen.
/// <para>
/// Screenshot-based review needs the foreground window, an unlocked session and a
/// desktop nobody else is using. In practice that fails often: another agent or an
/// installer takes focus mid-run, and the capture silently shows the wrong window.
/// This renders the visual tree directly instead, with the window parked off-screen,
/// so a review is a file-reading exercise rather than a race for the desktop.
/// </para>
/// <para>
/// Language and theme are applied at startup and captured one process at a time,
/// rather than switched in place. That matches how the app really starts — the
/// localization markup extension resolves when a page is parsed — so a capture
/// cannot show a state a user could never reach.
/// </para>
/// <para>
/// It renders the XAML tree, not the desktop: tray notifications, native file
/// pickers and system dialogs are out of reach and still need a real session.
/// </para>
/// </summary>
public static class ScreenCaptureHarness
{
    /// <summary>Where a parked window sits: far outside any plausible desktop.</summary>
    private static readonly PointInt32 OffScreen = new(-32000, -32000);

    public static async Task RunAsync(MainWindow window, ScreenCaptureOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        // Park the window rather than hide it. A minimized or hidden window can stop
        // realizing layout, and RenderTargetBitmap then captures nothing.
        window.AppWindow.Move(OffScreen);

        var routes = options.Pages.Count > 0
            ? options.Pages.Where(NavigationRoutes.TagToPage.ContainsKey).ToArray()
            : NavigationRoutes.TagToPage.Keys.ToArray();

        var language = options.Language ?? LocalizationCatalog.ActiveLanguage;
        var theme = options.Theme ?? "default";
        var captured = 0;

        foreach (var tag in routes)
        {
            var pageType = NavigationRoutes.TagToPage[tag];

            if (!await NavigateAndWaitAsync(window, pageType))
            {
                Console.WriteLine($"skip {tag}: navigation did not land");
                continue;
            }

            await SettleAsync(window, options.SettleMilliseconds);

            var path = Path.Combine(options.OutputDirectory, $"{tag}--{theme}--{language}.png");

            try
            {
                await CaptureAsync(window.CaptureRoot, path);
                captured++;
                Console.WriteLine($"captured {tag} -> {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                // One page that cannot render must not cost the whole run; the
                // remaining pages are still worth having.
                Console.WriteLine($"failed {tag}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{captured}/{routes.Length} page(s) captured into {options.OutputDirectory}");
    }

    /// <summary>
    /// Navigates and waits for the frame to actually show the new page.
    /// <para>
    /// Waiting on a delay alone is not enough, and failed quietly in the worst
    /// possible way: every capture was one page behind the file it was written to,
    /// so the images looked plausible and were simply mislabelled. Waiting for
    /// <c>Navigated</c> and then for the page's own <c>Loaded</c> makes the capture
    /// correct by construction rather than by timing.
    /// </para>
    /// </summary>
    private static async Task<bool> NavigateAndWaitAsync(MainWindow window, Type pageType)
    {
        var frame = window.CaptureFrame;

        // The window navigates to the dashboard while it is being constructed, so
        // asking for it again is a no-op that raises no Navigated event. Without
        // this the very first page of every run timed out and was skipped.
        if (frame.Content?.GetType() == pageType)
            return true;

        var navigated = new TaskCompletionSource();

        void OnNavigated(object sender, NavigationEventArgs e)
        {
            frame.Navigated -= OnNavigated;
            navigated.TrySetResult();
        }

        frame.Navigated += OnNavigated;

        if (!window.CaptureNavigateTo(pageType))
        {
            frame.Navigated -= OnNavigated;
            return false;
        }

        if (await Task.WhenAny(navigated.Task, Task.Delay(10000)) != navigated.Task)
        {
            frame.Navigated -= OnNavigated;
            return false;
        }

        if (frame.Content is FrameworkElement page && !page.IsLoaded)
        {
            var loaded = new TaskCompletionSource();

            void OnLoaded(object sender, RoutedEventArgs e)
            {
                page.Loaded -= OnLoaded;
                loaded.TrySetResult();
            }

            page.Loaded += OnLoaded;
            await Task.WhenAny(loaded.Task, Task.Delay(10000));
            page.Loaded -= OnLoaded;
        }

        return true;
    }

    /// <summary>
    /// Waits for the page to finish arranging and for its data to arrive.
    /// <para>
    /// Both halves are needed. Yielding until layout is clean catches the arrange
    /// pass; the delay covers the asynchronous load behind it, which no layout event
    /// announces.
    /// </para>
    /// </summary>
    private static async Task SettleAsync(MainWindow window, int milliseconds)
    {
        var completion = new TaskCompletionSource();

        void OnLayoutUpdated(object? sender, object e)
        {
            window.CaptureRoot.LayoutUpdated -= OnLayoutUpdated;
            completion.TrySetResult();
        }

        window.CaptureRoot.LayoutUpdated += OnLayoutUpdated;
        window.CaptureRoot.UpdateLayout();

        await Task.WhenAny(completion.Task, Task.Delay(2000));
        await Task.Delay(milliseconds);
    }

    private static async Task CaptureAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);

        var pixels = await bitmap.GetPixelsAsync();

        // IBuffer has no ToArray without the WinRT interop extensions; DataReader
        // is already in scope for the encode below and reads it directly.
        var pixelBytes = new byte[pixels.Length];
        DataReader.FromBuffer(pixels).ReadBytes(pixelBytes);

        // Encoded in memory and then written with plain file IO: turning a
        // FileStream into an IRandomAccessStream needs WinRT interop that is not
        // worth taking on for a developer tool.
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            pixelBytes);

        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        await File.WriteAllBytesAsync(path, bytes);
    }
}
