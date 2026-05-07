using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.SpaceMap;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class SpaceMapViewModel : ObservableObject
{
    private const int ChildLimit = 450;
    private readonly ISpaceMapRepository _spaceMapRepository;
    private readonly INavigationService _navigation;
    private readonly TreemapLayoutService _layoutService = new();
    private CancellationTokenSource? _loadCts;
    private IReadOnlyList<SpaceMapNode> _currentNodes = [];
    private double _layoutWidth = 960;
    private double _layoutHeight = 560;
    private bool _suppressSessionReload;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDeltaLoading;
    [ObservableProperty] private ScanSession? _selectedSession;
    [ObservableProperty] private string _currentFolderPath = string.Empty;
    [ObservableProperty] private string _statusText = "Select a completed scan to render disk usage.";
    [ObservableProperty] private string _deltaStatusText = "Scan Delta needs two completed scans with the same root.";
    [ObservableProperty] private long _minimumSizeBytes;
    [ObservableProperty] private double _minimumSizeMb;
    [ObservableProperty] private SpaceMapNodeKind? _kindFilter;
    [ObservableProperty] private SpaceMapLayoutNode? _selectedNode;
    [ObservableProperty] private ScanDeltaSummary? _deltaSummary;

    public ObservableCollection<ScanSession> Sessions { get; } = [];
    public ObservableCollection<SpaceMapLayoutNode> LayoutNodes { get; } = [];
    public ObservableCollection<ScanDeltaItem> GrowingFolders { get; } = [];
    public ObservableCollection<ScanDeltaItem> NewLargeFiles { get; } = [];
    public ObservableCollection<ScanDeltaItem> RemovedFiles { get; } = [];
    public ObservableCollection<string> Breadcrumbs { get; } = [];

    public bool HasSessions => Sessions.Count > 0;
    public bool HasNodes => LayoutNodes.Count > 0;
    public bool HasSelectedNode => SelectedNode is not null;
    public bool HasDelta => DeltaSummary?.HasComparison == true;
    public string CurrentFolderDisplay => string.IsNullOrWhiteSpace(CurrentFolderPath) ? "No folder selected" : CurrentFolderPath;
    public string SelectedNodeDisplayName => SelectedNode?.Node.DisplayName ?? "No item selected";
    public string SelectedNodePath => SelectedNode?.Node.FullPath ?? "Select a block in the treemap.";
    public string SelectedNodeSize => SelectedNode is null ? "—" : ByteSizeConverter.Format(SelectedNode.Node.SizeBytes);
    public string SelectedNodePercent => SelectedNode is null ? "—" : $"{SelectedNode.Node.PercentOfParent:N1}% of parent";

    public SpaceMapViewModel(ISpaceMapRepository spaceMapRepository, INavigationService navigation)
    {
        _spaceMapRepository = spaceMapRepository;
        _navigation = navigation;
    }

    public async Task LoadAsync(long? sessionId = null)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        await Task.Yield();
        try
        {
            Sessions.Clear();
            var sessions = await _spaceMapRepository.GetSessionRootCandidatesAsync(ct);
            foreach (var session in sessions)
                Sessions.Add(session);

            OnPropertyChanged(nameof(HasSessions));

            var targetSession = sessionId is not null
                ? Sessions.FirstOrDefault(session => session.Id == sessionId.Value) ?? Sessions.FirstOrDefault()
                : Sessions.FirstOrDefault();

            _suppressSessionReload = true;
            SelectedSession = targetSession;
            _suppressSessionReload = false;

            if (SelectedSession is null)
            {
                ResetMap("No completed scans are available.");
                return;
            }

            await LoadFolderAsync(SelectedSession.RootPath, ct);
            await LoadDeltaAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ResizeLayout(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return;

        _layoutWidth = width;
        _layoutHeight = height;
        Relayout();
    }

    partial void OnMinimumSizeMbChanged(double value)
    {
        MinimumSizeBytes = (long)Math.Max(0, value) * 1024L * 1024L;
        _ = ReloadCurrentFolderAsync();
    }

    partial void OnKindFilterChanged(SpaceMapNodeKind? value) => _ = ReloadCurrentFolderAsync();

    partial void OnSelectedNodeChanged(SpaceMapLayoutNode? value)
    {
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(SelectedNodeDisplayName));
        OnPropertyChanged(nameof(SelectedNodePath));
        OnPropertyChanged(nameof(SelectedNodeSize));
        OnPropertyChanged(nameof(SelectedNodePercent));
    }

    partial void OnSelectedSessionChanged(ScanSession? value)
    {
        if (_suppressSessionReload)
            return;

        if (value is null)
            return;

        _ = ReloadSelectedSessionAsync(value);
    }

    [RelayCommand]
    private async Task DrillIntoAsync(SpaceMapLayoutNode? layoutNode)
    {
        if (layoutNode?.Node.Kind != SpaceMapNodeKind.Folder)
        {
            SelectedNode = layoutNode;
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        await LoadFolderAsync(layoutNode.Node.FullPath, _loadCts.Token);
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(CurrentFolderPath))
            return;

        var root = SelectedSession.RootPath;
        if (string.Equals(CurrentFolderPath, root, StringComparison.OrdinalIgnoreCase))
            return;

        var parent = Path.GetDirectoryName(CurrentFolderPath);
        if (string.IsNullOrWhiteSpace(parent))
            parent = root;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        await LoadFolderAsync(parent, _loadCts.Token);
    }

    [RelayCommand]
    private void SetFilter(string? filter)
    {
        KindFilter = filter switch
        {
            "Folders" => SpaceMapNodeKind.Folder,
            "Files" => SpaceMapNodeKind.File,
            _ => null,
        };
    }

    [RelayCommand]
    private static void CopyPath(SpaceMapLayoutNode? layoutNode)
    {
        if (layoutNode is null)
            return;

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(layoutNode.Node.FullPath);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    [RelayCommand]
    private static void RevealInExplorer(SpaceMapLayoutNode? layoutNode)
    {
        if (layoutNode is null)
            return;

        var args = layoutNode.Node.Kind == SpaceMapNodeKind.File
            ? $"/select,\"{layoutNode.Node.FullPath}\""
            : $"\"{layoutNode.Node.FullPath}\"";

        Process.Start(new ProcessStartInfo("explorer.exe", args)
        {
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void SendToCleanupReview()
    {
        if (SelectedSession is not null)
            _navigation.NavigateTo(typeof(CleanupPage), SelectedSession.Id);
    }

    [RelayCommand]
    private void SendToDuplicateReview()
    {
        if (SelectedSession is not null)
            _navigation.NavigateTo(typeof(DuplicatesPage), SelectedSession.Id);
    }

    [RelayCommand]
    private void OpenInResults()
    {
        if (SelectedSession is not null)
            _navigation.NavigateTo(typeof(ResultsPage), SelectedSession.Id);
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (_currentNodes.Count == 0)
            return;

        var path = CreateExportPath("csv");
        var lines = new List<string> { "Kind,Path,SizeBytes,PercentOfParent,FileCount,FolderCount,Category,ModifiedUtc" };
        lines.AddRange(_currentNodes.Select(static node =>
            $"{node.Kind},\"{node.FullPath.Replace("\"", "\"\"")}\",{node.SizeBytes},{node.PercentOfParent:F3},{node.FileCount},{node.FolderCount},{node.Category},{node.ModifiedUtc:O}"));
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);
        StatusText = $"CSV report exported to {path}.";
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (_currentNodes.Count == 0)
            return;

        var path = CreateExportPath("html");
        var rows = string.Join(Environment.NewLine, _currentNodes.Select(static node =>
            $"<tr><td>{System.Net.WebUtility.HtmlEncode(node.Kind.ToString())}</td><td>{System.Net.WebUtility.HtmlEncode(node.FullPath)}</td><td>{node.SizeBytes:N0}</td><td>{node.PercentOfParent:N1}%</td><td>{System.Net.WebUtility.HtmlEncode(node.Category.ToString())}</td></tr>"));
        var html = $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>StorageMaster Space Map</title>
            <style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #ddd;padding:8px;text-align:left}</style>
            </head><body>
            <h1>StorageMaster Space Map</h1>
            <p>{{System.Net.WebUtility.HtmlEncode(CurrentFolderPath)}}</p>
            <table><thead><tr><th>Kind</th><th>Path</th><th>Bytes</th><th>Parent %</th><th>Category</th></tr></thead><tbody>
            {{rows}}
            </tbody></table></body></html>
            """;
        await File.WriteAllTextAsync(path, html, Encoding.UTF8);
        StatusText = $"HTML report exported to {path}.";
    }

    private async Task ReloadCurrentFolderAsync()
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(CurrentFolderPath))
            return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        await LoadFolderAsync(CurrentFolderPath, _loadCts.Token);
    }

    private async Task ReloadSelectedSessionAsync(ScanSession session)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        try
        {
            await LoadFolderAsync(session.RootPath, _loadCts.Token);
            await LoadDeltaAsync(_loadCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadFolderAsync(string folderPath, CancellationToken ct)
    {
        if (SelectedSession is null)
            return;

        IsLoading = true;
        try
        {
            CurrentFolderPath = folderPath;
            StatusText = "Loading folder children...";
            var nodes = await _spaceMapRepository.GetFolderChildrenWithSizesAsync(
                SelectedSession.Id,
                folderPath,
                KindFilter,
                MinimumSizeBytes,
                ChildLimit,
                ct);

            _currentNodes = nodes;
            SelectedNode = null;
            BuildBreadcrumbs(folderPath);
            Relayout();
            StatusText = nodes.Count == 0
                ? "No children match the current filters."
                : $"Showing {nodes.Count:N0} largest child item(s). Destructive actions route through cleanup or duplicate review.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadDeltaAsync(CancellationToken ct)
    {
        if (SelectedSession is null)
            return;

        IsDeltaLoading = true;
        try
        {
            var previous = await _spaceMapRepository.GetPreviousComparableSessionAsync(SelectedSession.Id, ct);
            if (previous is null)
            {
                DeltaSummary = new ScanDeltaSummary { CurrentSessionId = SelectedSession.Id };
                DeltaStatusText = "No previous completed scan with this root was found.";
                return;
            }

            DeltaSummary = await _spaceMapRepository.GetScanDeltaAsync(SelectedSession.Id, previous.Id, 25, ct);
            Replace(GrowingFolders, DeltaSummary.GrowingFolders);
            Replace(NewLargeFiles, DeltaSummary.NewLargeFiles);
            Replace(RemovedFiles, DeltaSummary.RemovedFiles);
            DeltaStatusText = $"Comparing against scan from {previous.StartedUtc:g}.";
            OnPropertyChanged(nameof(HasDelta));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsDeltaLoading = false;
        }
    }

    private void Relayout()
    {
        LayoutNodes.Clear();
        foreach (var node in _layoutService.Layout(_currentNodes, _layoutWidth, _layoutHeight))
            LayoutNodes.Add(node);

        OnPropertyChanged(nameof(HasNodes));
    }

    private void ResetMap(string message)
    {
        _currentNodes = [];
        LayoutNodes.Clear();
        Breadcrumbs.Clear();
        StatusText = message;
        OnPropertyChanged(nameof(HasNodes));
    }

    private void BuildBreadcrumbs(string folderPath)
    {
        Breadcrumbs.Clear();
        if (SelectedSession is null)
            return;

        var root = SelectedSession.RootPath;
        Breadcrumbs.Add(root);

        var relative = folderPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? folderPath[root.Length..].Trim('\\')
            : string.Empty;

        var cursor = root.TrimEnd('\\');
        foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = cursor + "\\" + segment;
            Breadcrumbs.Add(cursor);
        }
    }

    public string CreateExportPath(string extension)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster",
            "exports");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"space-map-{SelectedSession?.Id ?? 0}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}");
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
