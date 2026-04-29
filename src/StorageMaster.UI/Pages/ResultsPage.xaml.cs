using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class ResultsPage : Page
{
    public ResultsViewModel ViewModel { get; }

    public ResultsPage()
    {
        ViewModel = App.Services.GetRequiredService<ResultsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Give the ViewModel the XamlRoot so it can show ContentDialogs (e.g. delete confirm).
        ViewModel.XamlRoot = XamlRoot;

        if (e.Parameter is long sessionId && sessionId > 0)
            await ViewModel.LoadAsync(sessionId);
        else
            await ViewModel.LoadMostRecentAsync();

        // Populate the TreeView with WinUI-native TreeViewNode objects.
        // ViewModel.FolderTreeRoots holds POCOs; we map them here so the ViewModel
        // stays free of WinUI type dependencies.
        PopulateFolderTreeView();
    }

    /// <summary>
    /// Converts the POCO <see cref="FolderTreeNode"/> hierarchy produced by the ViewModel
    /// into <see cref="TreeViewNode"/> objects understood by the WinUI 3 <see cref="TreeView"/>.
    /// </summary>
    private void PopulateFolderTreeView()
    {
        FolderTreeControl.RootNodes.Clear();
        foreach (var root in ViewModel.FolderTreeRoots)
            FolderTreeControl.RootNodes.Add(ToTreeViewNode(root));
    }

    private static TreeViewNode ToTreeViewNode(FolderTreeNode node)
    {
        var tvNode = new TreeViewNode { Content = node, IsExpanded = false };
        foreach (var child in node.Children)
            tvNode.Children.Add(ToTreeViewNode(child));
        return tvNode;
    }
}
