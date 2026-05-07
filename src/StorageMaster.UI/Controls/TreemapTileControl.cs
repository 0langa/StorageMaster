using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using StorageMaster.Core.Models;
using StorageMaster.Core.SpaceMap;
using StorageMaster.UI.Converters;

namespace StorageMaster.UI.Controls;

public sealed class TreemapTileControl : Button
{
    public static readonly DependencyProperty LayoutNodeProperty =
        DependencyProperty.Register(nameof(LayoutNode), typeof(SpaceMapLayoutNode), typeof(TreemapTileControl), new PropertyMetadata(null, OnTileChanged));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TreemapTileControl), new PropertyMetadata(false, OnTileChanged));

    public TreemapTileControl()
    {
        Padding = new Thickness(6);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        UseSystemFocusVisuals = true;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        KeyDown += OnKeyDown;
    }

    public SpaceMapLayoutNode? LayoutNode
    {
        get => (SpaceMapLayoutNode?)GetValue(LayoutNodeProperty);
        set => SetValue(LayoutNodeProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private static void OnTileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreemapTileControl tile)
            tile.ApplyTile();
    }

    private void ApplyTile()
    {
        if (LayoutNode is null)
            return;

        Width = Math.Max(0, LayoutNode.Width - 3);
        Height = Math.Max(0, LayoutNode.Height - 3);
        Background = new SolidColorBrush(ColorFor(LayoutNode.Node));
        BorderBrush = new SolidColorBrush(IsSelected ? Colors.White : Colors.Transparent);
        BorderThickness = new Thickness(IsSelected ? 3 : 1);
        ToolTipService.SetToolTip(this, $"{LayoutNode.Node.FullPath}\n{ByteSizeConverter.Format(LayoutNode.Node.SizeBytes)} ({LayoutNode.Node.PercentOfParent:N1}% of parent)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"{LayoutNode.Node.Kind} {LayoutNode.Node.DisplayName}, {ByteSizeConverter.Format(LayoutNode.Node.SizeBytes)}");
        Content = BuildContent(LayoutNode);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Opacity = 0.92;
        BorderBrush = new SolidColorBrush(Colors.White);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        Opacity = 1;
        ApplyTile();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            Command?.Execute(CommandParameter);
            e.Handled = true;
        }
    }

    private static UIElement BuildContent(SpaceMapLayoutNode layout)
    {
        if (layout.Width < 64 || layout.Height < 32)
            return new Grid();

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = layout.Node.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        if (layout.Width >= 92 && layout.Height >= 52)
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
