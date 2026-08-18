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
    private long _loadGeneration;

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
    public bool CanInteract => !IsLoading;
    public bool CanExport => HasNodes && !IsLoading;
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

    public Task LoadAsync(long? sessionId = null, CancellationToken cancellationToken = default)
    {
        ResetMap("Loading completed scans...");
        return RunLoadAsync(async (generation, ct) =>
        {
            var sessions = await _spaceMapRepository.GetSessionRootCandidatesAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(generation))
                return;

            Sessions.Clear();
            foreach (var session in sessions)
                Sessions.Add(session);

            OnPropertyChanged(nameof(HasSessions));

            var targetSession = sessionId is not null
                ? Sessions.FirstOrDefault(session => session.Id == sessionId.Value)
                : Sessions.FirstOrDefault();

            _suppressSessionReload = true;
            SelectedSession = targetSession;
            _suppressSessionReload = false;

            if (targetSession is null)
            {
                ResetMap(sessionId is not null
                    ? "The requested completed scan is not available."
                    : "No completed scans are available.");
                return;
            }

            await LoadFolderCoreAsync(targetSession, targetSession.RootPath, generation, ct);
            await LoadDeltaCoreAsync(targetSession, generation, ct);
        }, "Space map session load failed", cancellationToken);
    }

    public void CancelPendingLoad() => _loadCts?.Cancel();

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
        ObserveReload(ReloadCurrentFolderAsync());
    }

    partial void OnKindFilterChanged(SpaceMapNodeKind? value) => ObserveReload(ReloadCurrentFolderAsync());

    partial void OnCurrentFolderPathChanged(string value) =>
        OnPropertyChanged(nameof(CurrentFolderDisplay));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanExport));
    }

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

        ResetMap("Loading the selected scan...");
        ObserveReload(ReloadSelectedSessionAsync(value));
    }

    [RelayCommand]
    private async Task DrillIntoAsync(SpaceMapLayoutNode? layoutNode)
    {
        if (layoutNode?.Node.Kind != SpaceMapNodeKind.Folder)
        {
            SelectedNode = layoutNode;
            return;
        }

        var session = SelectedSession;
        if (session is null)
            return;

        await RunLoadAsync(
            (generation, ct) => LoadFolderCoreAsync(session, layoutNode.Node.FullPath, generation, ct),
            "Folder load failed");
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

        var session = SelectedSession;
        if (session is null)
            return;
        await RunLoadAsync(
            (generation, ct) => LoadFolderCoreAsync(session, parent, generation, ct),
            "Parent folder load failed");
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
    private void CopyPath(SpaceMapLayoutNode? layoutNode)
    {
        if (layoutNode is null)
            return;

        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(layoutNode.Node.FullPath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            StatusText = "Path copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not copy the path: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RevealInExplorer(SpaceMapLayoutNode? layoutNode)
    {
        if (layoutNode is null)
            return;

        try
        {
            var args = layoutNode.Node.Kind == SpaceMapNodeKind.File
                ? $"/select,\"{layoutNode.Node.FullPath}\""
                : $"\"{layoutNode.Node.FullPath}\"";

            Process.Start(new ProcessStartInfo("explorer.exe", args)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open File Explorer: {ex.Message}";
        }
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
        if (!CanExport)
            return;

        try
        {
            var path = CreateExportPath("csv");
            var lines = new List<string> { "Kind,Path,SizeBytes,PercentOfParent,FileCount,FolderCount,Category,ModifiedUtc" };
            lines.AddRange(_currentNodes.Select(static node =>
                $"{node.Kind},\"{node.FullPath.Replace("\"", "\"\"")}\",{node.SizeBytes},{node.PercentOfParent:F3},{node.FileCount},{node.FolderCount},{node.Category},{node.ModifiedUtc:O}"));
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);
            StatusText = $"CSV report exported to {path}.";
        }
        catch (Exception ex)
        {
            StatusText = $"CSV export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (!CanExport)
            return;

        try
        {
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
        catch (Exception ex)
        {
            StatusText = $"HTML export failed: {ex.Message}";
        }
    }

    private Task ReloadCurrentFolderAsync()
    {
        var session = SelectedSession;
        var folderPath = CurrentFolderPath;
        if (session is null || string.IsNullOrWhiteSpace(folderPath))
            return Task.CompletedTask;

        return RunLoadAsync(
            (generation, ct) => LoadFolderCoreAsync(session, folderPath, generation, ct),
            "Folder reload failed");
    }

    private Task ReloadSelectedSessionAsync(ScanSession session)
    {
        return RunLoadAsync(async (generation, ct) =>
        {
            await LoadFolderCoreAsync(session, session.RootPath, generation, ct);
            await LoadDeltaCoreAsync(session, generation, ct);
        }, "Selected scan load failed");
    }

    private async Task LoadFolderCoreAsync(
        ScanSession session,
        string folderPath,
        long generation,
        CancellationToken ct)
    {
        if (!IsCurrentLoad(generation))
            return;

        var kindFilter = KindFilter;
        var minimumSizeBytes = MinimumSizeBytes;
        StatusText = "Loading folder children...";
        var nodes = await _spaceMapRepository.GetFolderChildrenWithSizesAsync(
            session.Id,
            folderPath,
            kindFilter,
            minimumSizeBytes,
            ChildLimit,
            ct);

        ct.ThrowIfCancellationRequested();
        if (!IsCurrentLoad(generation))
            return;

        _currentNodes = nodes;
        CurrentFolderPath = folderPath;
        SelectedNode = null;
        BuildBreadcrumbs(session, folderPath);
        Relayout();
        StatusText = nodes.Count == 0
            ? "No children match the current filters."
            : $"Showing {nodes.Count:N0} largest child item(s). Destructive actions route through cleanup or duplicate review.";
    }

    private async Task LoadDeltaCoreAsync(
        ScanSession session,
        long generation,
        CancellationToken ct)
    {
        if (!IsCurrentLoad(generation))
            return;

        IsDeltaLoading = true;
        try
        {
            var previous = await _spaceMapRepository.GetPreviousComparableSessionAsync(session.Id, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(generation))
                return;

            if (previous is null)
            {
                DeltaSummary = new ScanDeltaSummary { CurrentSessionId = session.Id };
                GrowingFolders.Clear();
                NewLargeFiles.Clear();
                RemovedFiles.Clear();
                DeltaStatusText = "No previous completed scan with this root was found.";
                OnPropertyChanged(nameof(HasDelta));
                return;
            }

            var deltaSummary = await _spaceMapRepository.GetScanDeltaAsync(session.Id, previous.Id, 25, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(generation))
                return;

            DeltaSummary = deltaSummary;
            Replace(GrowingFolders, deltaSummary.GrowingFolders);
            Replace(NewLargeFiles, deltaSummary.NewLargeFiles);
            Replace(RemovedFiles, deltaSummary.RemovedFiles);
            DeltaStatusText = $"Comparing against scan from {previous.StartedUtc:g}.";
            OnPropertyChanged(nameof(HasDelta));
        }
        finally
        {
            if (IsCurrentLoad(generation))
                IsDeltaLoading = false;
        }
    }

    private async Task RunLoadAsync(
        Func<long, CancellationToken, Task> operation,
        string failurePrefix,
        CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        _loadCts?.Cancel();
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCts = loadCancellation;
        IsDeltaLoading = false;
        IsLoading = true;
        SelectedNode = null;

        try
        {
            await Task.Yield();
            await operation(generation, loadCancellation.Token);
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
            // Superseded loads are expected and must not escape an AsyncRelayCommand.
        }
        catch (Exception ex)
        {
            if (IsCurrentLoad(generation))
                StatusText = $"{failurePrefix}: {ex.Message}";
        }
        finally
        {
            if (IsCurrentLoad(generation))
            {
                IsDeltaLoading = false;
                IsLoading = false;
                if (ReferenceEquals(_loadCts, loadCancellation))
                    _loadCts = null;
            }

            loadCancellation.Dispose();
        }
    }

    private bool IsCurrentLoad(long generation) =>
        generation == Volatile.Read(ref _loadGeneration);

    private void ObserveReload(Task task) => _ = ObserveReloadAsync(task);

    private async Task ObserveReloadAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            // RunLoadAsync contains repository failures. This final observer
            // protects future reload implementations from becoming naked tasks.
            StatusText = $"Space map reload failed: {ex.Message}";
        }
    }

    private void Relayout()
    {
        LayoutNodes.Clear();
        foreach (var node in _layoutService.Layout(_currentNodes, _layoutWidth, _layoutHeight))
            LayoutNodes.Add(node);

        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(CanExport));
    }

    private void ResetMap(string message)
    {
        _currentNodes = [];
        LayoutNodes.Clear();
        Breadcrumbs.Clear();
        CurrentFolderPath = string.Empty;
        SelectedNode = null;
        DeltaSummary = null;
        GrowingFolders.Clear();
        NewLargeFiles.Clear();
        RemovedFiles.Clear();
        DeltaStatusText = "Scan Delta needs two completed scans with the same root.";
        StatusText = message;
        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(HasDelta));
        OnPropertyChanged(nameof(CanExport));
    }

    private void BuildBreadcrumbs(ScanSession session, string folderPath)
    {
        Breadcrumbs.Clear();
        var root = session.RootPath;
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
