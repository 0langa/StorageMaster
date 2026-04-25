using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;

namespace StorageMaster.UI.Pages;

public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly IScanRepository _repo;

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _scanRoot    = string.Empty;
    [ObservableProperty] private string _scanDate    = string.Empty;
    [ObservableProperty] private string _totalSize   = "—";
    [ObservableProperty] private long   _totalFiles;
    [ObservableProperty] private string _filterText  = string.Empty;

    private long _sessionId;
    private IReadOnlyList<FileEntry>   _allFiles   = [];
    private IReadOnlyList<FolderEntry> _allFolders = [];

    public ObservableCollection<FileEntry>   LargestFiles   { get; } = [];
    public ObservableCollection<FolderEntry> LargestFolders { get; } = [];
    public ObservableCollection<CategoryRow> CategoryBreakdown { get; } = [];

    public ResultsViewModel(IScanRepository repo) => _repo = repo;

    public async Task LoadAsync(long sessionId)
    {
        _sessionId = sessionId;
        IsLoading  = true;

        try
        {
            var session = await _repo.GetSessionAsync(sessionId);
            if (session is not null)
            {
                ScanRoot   = session.RootPath;
                ScanDate   = session.CompletedUtc?.ToString("g") ?? session.StartedUtc.ToString("g");
                TotalSize  = ByteSizeConverter.Format(session.TotalSizeBytes);
                TotalFiles = session.TotalFiles;
            }

            _allFiles   = await _repo.GetLargestFilesAsync(sessionId,   topN: 500);
            _allFolders = await _repo.GetLargestFoldersAsync(sessionId,  topN: 200);

            ApplyFilter();

            var breakdown = await _repo.GetCategoryBreakdownAsync(sessionId);
            CategoryBreakdown.Clear();
            foreach (var (cat, (count, bytes)) in breakdown.OrderByDescending(x => x.Value.Bytes))
            {
                CategoryBreakdown.Add(new CategoryRow(cat.ToString(), count, ByteSizeConverter.Format(bytes)));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ApplyFilter()
    {
        var filter = FilterText.Trim();

        LargestFiles.Clear();
        foreach (var f in _allFiles
            .Where(f => string.IsNullOrEmpty(filter) ||
                        f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(200))
        {
            LargestFiles.Add(f);
        }

        LargestFolders.Clear();
        foreach (var f in _allFolders
            .Where(f => string.IsNullOrEmpty(filter) ||
                        f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(100))
        {
            LargestFolders.Add(f);
        }
    }
}

public sealed record CategoryRow(string Category, long FileCount, string TotalSize);
