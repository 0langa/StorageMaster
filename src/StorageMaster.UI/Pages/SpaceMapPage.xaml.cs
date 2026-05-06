using System.Collections.Specialized;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using StorageMaster.Core.Models;
using StorageMaster.Core.SpaceMap;
using StorageMaster.UI.Converters;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StorageMaster.UI.Pages;

public sealed partial class SpaceMapPage : Page
{
    private readonly ILogger<SpaceMapPage> _logger;
    public SpaceMapViewModel ViewModel { get; }

    public SpaceMapPage()
    {
        _logger = App.Services.GetRequiredService<ILogger<SpaceMapPage>>();
        ViewModel = App.Services.GetRequiredService<SpaceMapViewModel>();
        InitializeComponent();
        ViewModel.LayoutNodes.CollectionChanged += LayoutNodes_CollectionChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            var sessionId = e.Parameter switch
            {
                long id => id,
                int id => id,
                _ => (long?)null,
            };
            await ViewModel.LoadAsync(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Space map session load failed");
            ViewModel.StatusText = "Failed to load session data.";
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.LayoutNodes.CollectionChanged -= LayoutNodes_CollectionChanged;
    }

    private void LayoutNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RenderTreemap();

    private void TreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.ResizeLayout(e.NewSize.Width, e.NewSize.Height);
        RenderTreemap();
    }

    private void RenderTreemap()
    {
        if (TreemapCanvas is null)
            return;

        TreemapCanvas.Children.Clear();
        foreach (var layout in ViewModel.LayoutNodes)
        {
            if (layout.Width < 2 || layout.Height < 2)
                continue;

            var button = new Button
            {
                Width = Math.Max(0, layout.Width - 3),
                Height = Math.Max(0, layout.Height - 3),
                Padding = new Thickness(6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(ColorFor(layout.Node)),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(1),
                Content = BuildTileContent(layout),
                Command = ViewModel.DrillIntoCommand,
                CommandParameter = layout,
                ContextFlyout = BuildFlyout(layout),
            };

            ToolTipService.SetToolTip(
                button,
                $"{layout.Node.FullPath}\n{ByteSizeConverter.Format(layout.Node.SizeBytes)} ({layout.Node.PercentOfParent:N1}% of parent)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                $"{layout.Node.Kind} {layout.Node.DisplayName}, {ByteSizeConverter.Format(layout.Node.SizeBytes)}");

            button.Tapped += (_, _) => ViewModel.SelectedNode = layout;
            button.DoubleTapped += (_, _) => ViewModel.DrillIntoCommand.Execute(layout);

            Canvas.SetLeft(button, layout.X);
            Canvas.SetTop(button, layout.Y);
            TreemapCanvas.Children.Add(button);
        }
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (TreemapCanvas.Children.Count == 0)
        {
            ViewModel.StatusText = "Nothing to export. Load a scan folder first.";
            return;
        }

        try
        {
            var path = ViewModel.CreateExportPath("png");
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(TreemapCanvas);
            var pixels = await bitmap.GetPixelsAsync();

            await using var fileStream = File.Create(path);
            using var randomAccessStream = fileStream.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccessStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                pixels.ToArray());
            await encoder.FlushAsync();

            ViewModel.StatusText = $"PNG screenshot exported to {path}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PNG export failed");
            ViewModel.StatusText = $"PNG export failed: {ex.Message}";
        }
    }

    private UIElement BuildTileContent(SpaceMapLayoutNode layout)
    {
        var showDetails = layout.Width >= 88 && layout.Height >= 48;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = layout.Node.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        if (showDetails)
        {
            panel.Children.Add(new TextBlock
            {
                Text = ByteSizeConverter.Format(layout.Node.SizeBytes),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.78,
                FontSize = 12,
            });
        }

        return panel;
    }

    private MenuFlyout BuildFlyout(SpaceMapLayoutNode layout)
    {
        var flyout = new MenuFlyout();
        if (layout.Node.Kind == SpaceMapNodeKind.Folder)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Drill into folder",
                Command = ViewModel.DrillIntoCommand,
                CommandParameter = layout,
            });
        }

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Copy path",
            Command = ViewModel.CopyPathCommand,
            CommandParameter = layout,
        });
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Reveal in Explorer",
            Command = ViewModel.RevealInExplorerCommand,
            CommandParameter = layout,
        });
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Send to Cleanup review",
            Command = ViewModel.SendToCleanupReviewCommand,
        });
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Send to Duplicate review",
            Command = ViewModel.SendToDuplicateReviewCommand,
        });
        return flyout;
    }

    private static Windows.UI.Color ColorFor(SpaceMapNode node) => node.Kind == SpaceMapNodeKind.Folder
        ? Colors.SlateBlue
        : node.Category switch
        {
            FileTypeCategory.Image => Colors.SeaGreen,
            FileTypeCategory.Video => Colors.IndianRed,
            FileTypeCategory.Audio => Colors.DarkCyan,
            FileTypeCategory.Archive => Colors.DarkGoldenrod,
            FileTypeCategory.Executable => Colors.DimGray,
            FileTypeCategory.Document => Colors.RoyalBlue,
            FileTypeCategory.SourceCode => Colors.Teal,
            FileTypeCategory.Cache or FileTypeCategory.Temporary => Colors.Peru,
            _ => Colors.Gray,
        };
}
