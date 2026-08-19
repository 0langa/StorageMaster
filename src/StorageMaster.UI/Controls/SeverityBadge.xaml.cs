using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Controls;

public sealed partial class SeverityBadge : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(SeverityBadge), new PropertyMetadata("Unknown", OnStateChanged));

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(DriveHealthStatus), typeof(SeverityBadge), new PropertyMetadata(DriveHealthStatus.Unknown, OnStateChanged));

    public SeverityBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyState();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public DriveHealthStatus Status
    {
        get => (DriveHealthStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string DisplayText => string.IsNullOrWhiteSpace(Text) ? Status.ToString() : Text;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SeverityBadge badge)
            badge.ApplyState();
    }

    /// <summary>
    /// Paints both halves of the badge's contrast pair from the same severity step.
    /// <para>
    /// The filled version set only the background and let the label inherit the theme
    /// foreground, so one half of the pair moved with the theme and the other did not:
    /// the same chip was readable in dark and failed AA in light. The chip is outlined
    /// instead — ring and label take the severity <em>text</em> colour, which
    /// <c>PaletteUsageContrastTests</c> proves is readable on every surface the app
    /// paints, in both themes — while the dot keeps the fill colour so the state is
    /// still scannable without reading the word.
    /// </para>
    /// <para>
    /// The brushes are looked up rather than copied: <c>ThemeService</c> mutates the
    /// live brush objects in place, so a badge that holds the resource follows an
    /// accent or theme change without being reloaded.
    /// </para>
    /// </summary>
    private void ApplyState()
    {
        Bindings.Update();
        var (fillKey, textKey) = Status switch
        {
            DriveHealthStatus.Healthy => ("SeverityHealthyBrush", "SeverityHealthyTextBrush"),
            DriveHealthStatus.Warning => ("SeverityWarningBrush", "SeverityWarningTextBrush"),
            DriveHealthStatus.Critical => ("SeverityCriticalBrush", "SeverityCriticalTextBrush"),
            DriveHealthStatus.Unsupported => ("SeverityUnknownBrush", "SeverityUnknownTextBrush"),
            _ => ("SeverityUnknownBrush", "SeverityUnknownTextBrush"),
        };

        var textBrush = FindBrush(textKey);
        BadgeBorder.BorderBrush = textBrush;
        BadgeTextBlock.Foreground = textBrush;
        BadgeDot.Fill = FindBrush(fillKey);
    }

    private static Brush? FindBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) ? resource as Brush : null;
}
