using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

/// <summary>
/// Wraps a SmartCleanGroup with UI state (selected toggle, formatted size).
/// </summary>
public sealed partial class SmartCleanGroupItem : ObservableObject
{
    public SmartCleanGroup Group { get; }

    [ObservableProperty] private bool _isSelected;

    public string Category => Group.Category;
    public string Description => Group.Description;
    public string IconGlyph => Group.IconGlyph;
    public string SizeDisplay => ByteSizeConverter.Format(Group.EstimatedBytes);

    public SmartCleanGroupItem(SmartCleanGroup group)
    {
        Group = group;
        _isSelected = group.IsSelected;
    }
}

public sealed partial class SmartCleanerViewModel : ObservableObject
{
    private readonly ISmartCleanerService _service;
    private readonly ISettingsRepository _settings;
    private readonly IDialogService _dialogs;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _operationCts;
    private long _operationVersion;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _cleaningDone;
    [ObservableProperty] private string _statusText = Loc.Get("Smart_Status_Initial");
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _totalSizeText = string.Empty;
    [ObservableProperty] private string _freedText = string.Empty;
    [ObservableProperty] private bool _useRecycleBin = true;
    [ObservableProperty] private int _selectedGroupCount;
    [ObservableProperty] private InfoBarSeverity _completionSeverity = InfoBarSeverity.Success;
    [ObservableProperty] private bool _hasFailureDetails;

    public bool CanClean => HasResults && !IsScanning && !IsCleaning && SelectedGroupCount > 0;
    public bool CanAnalyze => !IsScanning && !IsCleaning;
    public bool IsBusy => IsScanning || IsCleaning;

    partial void OnHasResultsChanged(bool value) => OnPropertyChanged(nameof(CanClean));
    partial void OnIsScanningChanged(bool value) => NotifyOperationStateChanged();
    partial void OnIsCleaningChanged(bool value) => NotifyOperationStateChanged();
    partial void OnSelectedGroupCountChanged(int value) => OnPropertyChanged(nameof(CanClean));

    public ObservableCollection<SmartCleanGroupItem> Groups { get; } = [];
    public ObservableCollection<string> FailureDetails { get; } = [];

    public SmartCleanerViewModel(
        ISmartCleanerService service,
        ISettingsRepository settings,
        IDialogService dialogs)
    {
        _service = service;
        _settings = settings;
        _dialogs = dialogs;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync()
    {
        var settings = await _settings.LoadAsync(_lifetimeCts.Token);
        UseRecycleBin = settings.PreferRecycleBin;
    }

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        if (!CanAnalyze)
            return;

        var (operationVersion, operationCts) = BeginOperation();
        IsScanning = true;
        HasResults = false;
        CleaningDone = false;
        FreedText = string.Empty;
        ClearFailureDetails();
        UnsubscribeGroups();
        Groups.Clear();
        SelectedGroupCount = 0;
        StatusText = Loc.Get("Smart_Status_Scanning");

        var dispatcherQueue = _dispatcherQueue;
        var progress = new Progress<string>(message =>
        {
            void Apply()
            {
                if (IsCurrentOperation(operationVersion, operationCts))
                    ProgressText = message;
            }

            if (dispatcherQueue.HasThreadAccess) Apply(); else dispatcherQueue.TryEnqueue(Apply);
        });

        try
        {
            var analysis = await _service.AnalyzeAsync(progress, operationCts.Token);
            if (!IsCurrentOperation(operationVersion, operationCts))
                return;

            foreach (var group in analysis.Groups)
            {
                var item = new SmartCleanGroupItem(group);
                item.PropertyChanged += OnGroupItemPropertyChanged;
                Groups.Add(item);
            }

            foreach (var warning in analysis.Warnings)
                FailureDetails.Add(Loc.Format(
                    "Smart_FailureDetails_ScanWarning",
                    warning.Path,
                    warning.Message));
            HasFailureDetails = FailureDetails.Count > 0;

            UpdateTotalSize();
            HasResults = Groups.Count > 0;
            StatusText = (Groups.Count, analysis.IsPartial) switch
            {
                ( > 0, true) => Loc.Format(
                    "Smart_Status_FoundPartial",
                    Groups.Count,
                    analysis.Warnings.Count),
                ( > 0, false) => Loc.Format("Smart_Status_Found", Groups.Count),
                (0, true) => Loc.Format(
                    "Smart_Status_ScanIncomplete",
                    analysis.Warnings.Count),
                _ => Loc.Get("Smart_Status_NoJunk"),
            };
            ProgressText = string.Empty;
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            if (IsCurrentOperation(operationVersion, operationCts))
            {
                StatusText = Loc.Get("Smart_Status_ScanCancelled");
                ProgressText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentOperation(operationVersion, operationCts))
            {
                StatusText = Loc.Format("Smart_Status_ScanFailed", ex.Message);
                ProgressText = string.Empty;
            }
        }
        finally
        {
            if (CompleteOperation(operationVersion, operationCts))
                IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (!CanClean)
            return;

        var selected = Groups
            .Where(group => group.IsSelected)
            .Select(group => group.Group)
            .ToList();
        if (selected.Count == 0)
            return;

        var (operationVersion, operationCts) = BeginOperation();
        IsCleaning = true;
        CleaningDone = false;
        CompletionSeverity = InfoBarSeverity.Success;
        FreedText = string.Empty;
        ClearFailureDetails();
        StatusText = Loc.Get("Smart_Status_AwaitingConfirmation");
        var cleanupStarted = false;

        var dispatcherQueue = _dispatcherQueue;
        var progress = new Progress<string>(message =>
        {
            void Apply()
            {
                if (IsCurrentOperation(operationVersion, operationCts))
                    ProgressText = message;
            }

            if (dispatcherQueue.HasThreadAccess) Apply(); else dispatcherQueue.TryEnqueue(Apply);
        });

        try
        {
            var totalBytes = selected.Sum(static group => group.EstimatedBytes);
            var confirmationText = UseRecycleBin
                ? Loc.Format(
                    "Safety_Smart_Confirm_RecycleBin_Message",
                    selected.Count,
                    ByteSizeConverter.Format(totalBytes))
                : Loc.Format(
                    "Safety_Smart_Confirm_Permanent_Message",
                    selected.Count,
                    ByteSizeConverter.Format(totalBytes));
            var confirmed = await _dialogs.ConfirmAsync(
                Loc.Get("Safety_Smart_Confirm_Title"),
                confirmationText,
                UseRecycleBin
                    ? Loc.Get("Safety_Smart_Confirm_MoveButton")
                    : Loc.Get("Safety_Smart_Confirm_DeleteButton"));

            if (!confirmed || !IsCurrentOperation(operationVersion, operationCts))
                return;

            var method = UseRecycleBin ? DeletionMethod.RecycleBin : DeletionMethod.Permanent;
            StatusText = Loc.Get("Smart_Status_Cleaning");
            cleanupStarted = true;
            var result = await _service.CleanAsync(selected, method, progress, operationCts.Token);
            if (!IsCurrentOperation(operationVersion, operationCts))
                return;

            ApplyCleanupResult(result, method);
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            if (IsCurrentOperation(operationVersion, operationCts))
            {
                if (cleanupStarted)
                    InvalidateGroups();
                CompletionSeverity = InfoBarSeverity.Warning;
                StatusText = Loc.Get("Smart_Status_CleanupCancelled");
                CleaningDone = true;
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentOperation(operationVersion, operationCts))
            {
                if (cleanupStarted)
                    InvalidateGroups();
                FailureDetails.Add(Loc.Format("Smart_FailureDetails_CleanupError", ex.Message));
                HasFailureDetails = true;
                CompletionSeverity = InfoBarSeverity.Error;
                StatusText = Loc.Get("Smart_Status_CleanupFailed");
                CleaningDone = true;
            }
        }
        finally
        {
            if (CompleteOperation(operationVersion, operationCts))
            {
                IsCleaning = false;
                ProgressText = string.Empty;
            }
        }
    }

    [RelayCommand]
    private void CancelCurrent()
    {
        if (_operationCts is not { IsCancellationRequested: false } operationCts)
            return;

        StatusText = IsCleaning
            ? Loc.Get("Smart_Status_CancellingCleanup")
            : Loc.Get("Smart_Status_CancellingScan");
        operationCts.Cancel();
    }

    public void UpdateTotalSize()
    {
        long total = Groups.Where(group => group.IsSelected).Sum(group => group.Group.EstimatedBytes);
        TotalSizeText = ByteSizeConverter.Format(total);
        SelectedGroupCount = Groups.Count(group => group.IsSelected);
    }

    public void CancelOperations()
    {
        Interlocked.Increment(ref _operationVersion);
        _lifetimeCts.Cancel();
        Interlocked.Exchange(ref _operationCts, null)?.Cancel();
        UnsubscribeGroups();
    }

    private void ApplyCleanupResult(SmartCleanResult result, DeletionMethod method)
    {
        InvalidateGroups();
        FreedText = ByteSizeConverter.Format(result.BytesFreed);

        foreach (var failure in result.Failures)
            FailureDetails.Add($"{failure.Path}: {failure.Error}");
        foreach (var warning in result.AuditWarnings)
            FailureDetails.Add(Loc.Format("Smart_FailureDetails_AuditWarning", warning));
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            FailureDetails.Add(Loc.Format("Smart_FailureDetails_CleanupError", result.ErrorMessage));
        HasFailureDetails = FailureDetails.Count > 0;

        var processedText = ByteSizeConverter.Format(result.BytesProcessed);
        var operationSummary = method == DeletionMethod.RecycleBin
            ? Loc.Format("Safety_Smart_Summary_RecycleBin", processedText)
            : Loc.Format("Safety_Smart_Summary_Permanent", processedText, FreedText);

        if (result.SuccessfulPathCount == 0)
        {
            CompletionSeverity = result.WasCancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            StatusText = result.WasCancelled
                ? Loc.Get("Smart_Status_CancelledNothingRemoved")
                : Loc.Format("Smart_Status_NothingRemoved", result.Failures.Count);
        }
        else if (result.WasCancelled)
        {
            CompletionSeverity = InfoBarSeverity.Warning;
            StatusText = Loc.Format("Smart_Status_CancelledPartial", operationSummary);
        }
        else if (result.Failures.Count > 0 || result.ErrorMessage is not null)
        {
            CompletionSeverity = InfoBarSeverity.Warning;
            StatusText = Loc.Format(
                "Smart_Status_PartiallyCompleted",
                operationSummary,
                result.Failures.Count);
        }
        else if (result.AuditWarnings.Count > 0)
        {
            CompletionSeverity = InfoBarSeverity.Warning;
            StatusText = Loc.Format("Smart_Status_CompletedWithAuditWarning", operationSummary);
        }
        else
        {
            CompletionSeverity = InfoBarSeverity.Success;
            StatusText = Loc.Format("Smart_Status_Completed", operationSummary);
        }

        CleaningDone = true;
    }

    private void InvalidateGroups()
    {
        HasResults = false;
        SelectedGroupCount = 0;
        UnsubscribeGroups();
        Groups.Clear();
    }

    private void ClearFailureDetails()
    {
        FailureDetails.Clear();
        HasFailureDetails = false;
    }

    private void OnGroupItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SmartCleanGroupItem.IsSelected))
            UpdateTotalSize();
    }

    private void UnsubscribeGroups()
    {
        foreach (var group in Groups)
            group.PropertyChanged -= OnGroupItemPropertyChanged;
    }

    private (long Version, CancellationTokenSource Cts) BeginOperation()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var previous = Interlocked.Exchange(ref _operationCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        return (Interlocked.Increment(ref _operationVersion), cts);
    }

    private bool IsCurrentOperation(long version, CancellationTokenSource cts) =>
        version == Volatile.Read(ref _operationVersion) &&
        ReferenceEquals(Volatile.Read(ref _operationCts), cts);

    private bool CompleteOperation(long version, CancellationTokenSource cts)
    {
        var completedCurrent = IsCurrentOperation(version, cts) &&
            ReferenceEquals(Interlocked.CompareExchange(ref _operationCts, null, cts), cts);
        cts.Dispose();
        return completedCurrent;
    }

    private void NotifyOperationStateChanged()
    {
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(IsBusy));
    }
}
