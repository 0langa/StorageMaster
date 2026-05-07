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

    private void ApplyState()
    {
        Bindings.Update();
        var brushKey = Status switch
        {
            DriveHealthStatus.Healthy => "SeverityHealthyBrush",
            DriveHealthStatus.Warning => "SeverityWarningBrush",
            DriveHealthStatus.Critical => "SeverityCriticalBrush",
            DriveHealthStatus.Unsupported => "SeverityUnknownBrush",
            _ => "SeverityUnknownBrush",
        };
        BadgeBorder.Background = Application.Current.Resources[brushKey] as Brush;
    }
}
