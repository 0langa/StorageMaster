using System.Collections.Specialized;
using System.ComponentModel;
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
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;
using StorageMaster.Core.SpaceMap;
using StorageMaster.UI.Controls;
using StorageMaster.UI.Converters;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StorageMaster.UI.Pages;

public sealed partial class SpaceMapPage : Page
{
    private readonly ILogger<SpaceMapPage> _logger;
    private bool _renderQueued;
    private CancellationTokenSource? _loadCancellation;
    public SpaceMapViewModel ViewModel { get; }

    public SpaceMapPage()
    {
        _logger = App.Services.GetRequiredService<ILogger<SpaceMapPage>>();
        ViewModel = App.Services.GetRequiredService<SpaceMapViewModel>();
        InitializeComponent();
        ViewModel.LayoutNodes.CollectionChanged += LayoutNodes_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loadCancellation?.Cancel();
        var loadCancellation = new CancellationTokenSource();
        _loadCancellation = loadCancellation;
        var cancellationToken = loadCancellation.Token;
        try
        {
            var sessionId = e.Parameter switch
            {
                long id => id,
                int id => id,
                _ => (long?)null,
            };
            await ViewModel.LoadAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation cancellation is expected and owns no visible error.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Space map session load failed");
            if (ReferenceEquals(_loadCancellation, loadCancellation))
                ViewModel.StatusText = Loc.Get("SpaceMap_Error_SessionDataLoad");
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, loadCancellation))
                _loadCancellation = null;
            loadCancellation.Dispose();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        ViewModel.CancelPendingLoad();
        ViewModel.LayoutNodes.CollectionChanged -= LayoutNodes_CollectionChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void LayoutNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueRenderTreemap();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedNode))
            QueueRenderTreemap();
    }

    private void TreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.ResizeLayout(e.NewSize.Width, e.NewSize.Height);
        QueueRenderTreemap();
    }

    private void QueueRenderTreemap()
    {
        if (_renderQueued)
            return;

        _renderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _renderQueued = false;
            RenderTreemap();
        });
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

            var button = new TreemapTileControl
            {
                LayoutNode = layout,
                IsSelected = ReferenceEquals(layout, ViewModel.SelectedNode),
                Command = ViewModel.DrillIntoCommand,
                CommandParameter = layout,
                ContextFlyout = BuildFlyout(layout),
            };

            button.Tapped += (_, _) => ViewModel.SelectedNode = layout;
            button.DoubleTapped += (_, _) => ViewModel.DrillIntoCommand.Execute(layout);

            Canvas.SetLeft(button, layout.X);
            Canvas.SetTop(button, layout.Y);
            TreemapCanvas.Children.Add(button);
        }
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanExport || TreemapCanvas.Children.Count == 0)
        {
            ViewModel.StatusText = Loc.Get("SpaceMap_Export_NothingToExport");
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

            ViewModel.StatusText = Loc.Format("SpaceMap_Export_PngSucceeded", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PNG export failed");
            ViewModel.StatusText = Loc.Format("SpaceMap_Export_PngFailed", ex.Message);
        }
    }

    private MenuFlyout BuildFlyout(SpaceMapLayoutNode layout)
    {
        var flyout = new MenuFlyout();
        if (layout.Node.Kind == SpaceMapNodeKind.Folder)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = Loc.Get("SpaceMap_Action_DrillIntoFolder"),
                Command = ViewModel.DrillIntoCommand,
                CommandParameter = layout,
            });
        }

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = Loc.Get("SpaceMap_Action_CopyPath"),
            Command = ViewModel.CopyPathCommand,
            CommandParameter = layout,
        });
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = Loc.Get("SpaceMap_Action_RevealInExplorer"),
            Command = ViewModel.RevealInExplorerCommand,
            CommandParameter = layout,
        });
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = Loc.Get("SpaceMap_Action_SendToCleanup"),
            Command = ViewModel.SendToCleanupReviewCommand,
        });
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = Loc.Get("SpaceMap_Action_SendToDuplicate"),
            Command = ViewModel.SendToDuplicateReviewCommand,
        });
        return flyout;
    }

}
