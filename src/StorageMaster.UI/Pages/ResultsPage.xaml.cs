using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class ResultsPage : Page
{
    private static readonly FolderTreeNode PlaceholderFolderNode = new(new StorageMaster.Core.Models.FolderEntry
    {
        Id = -1,
        SessionId = -1,
        FullPath = string.Empty,
        FolderName = "Loading",
        DirectSizeBytes = 0,
        TotalSizeBytes = 0,
        FileCount = 0,
        SubFolderCount = 0,
        IsReparsePoint = false,
        WasAccessDenied = false,
    });

    public ResultsViewModel ViewModel { get; }

    public ResultsPage()
    {
        ViewModel = App.Services.GetRequiredService<ResultsViewModel>();
        InitializeComponent();
        ViewModel.CategoryFilterApplied += OnCategoryFilterApplied;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            if (e.Parameter is long sessionId && sessionId > 0)
                await ViewModel.LoadAsync(sessionId);
            else
                await ViewModel.LoadMostRecentAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CategoryFilterApplied -= OnCategoryFilterApplied;
        ViewModel.CancelBackgroundWork();
        base.OnNavigatedFrom(e);
    }

    /// <summary>
    /// Switches to the Largest Files tab after a category filter is applied from File Types,
    /// so the user immediately sees the filtered file list.
    /// </summary>
    private void OnCategoryFilterApplied(object? sender, EventArgs e)
    {
        ResultsPivot.SelectedIndex = 0;
    }

    private async void ResultsPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (ReferenceEquals(ResultsPivot.SelectedItem as PivotItem, ErrorsPivotItem))
                await ViewModel.EnsureErrorsLoadedAsync();

            if (ReferenceEquals(ResultsPivot.SelectedItem as PivotItem, FolderTreePivotItem))
            {
                await ViewModel.EnsureFolderTreeLoadedAsync();
                PopulateFolderTreeView();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void FolderTreeControl_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not FolderTreeNode model || model.AreChildrenLoaded)
            return;

        try
        {
            await ViewModel.LoadFolderChildrenAsync(model);
            args.Node.Children.Clear();
            foreach (var child in model.Children)
                args.Node.Children.Add(ToTreeViewNode(child));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void PopulateFolderTreeView()
    {
        FolderTreeControl.RootNodes.Clear();
        foreach (var root in ViewModel.FolderTreeRoots)
            FolderTreeControl.RootNodes.Add(ToTreeViewNode(root));
    }

    private static TreeViewNode ToTreeViewNode(FolderTreeNode node)
    {
        var treeNode = new TreeViewNode
        {
            Content = node,
            IsExpanded = false,
        };

        if (node.HasChildren && !node.AreChildrenLoaded)
        {
            treeNode.Children.Add(new TreeViewNode
            {
                Content = PlaceholderFolderNode,
            });
        }
        else
        {
            foreach (var child in node.Children)
                treeNode.Children.Add(ToTreeViewNode(child));
        }

        return treeNode;
    }
}
