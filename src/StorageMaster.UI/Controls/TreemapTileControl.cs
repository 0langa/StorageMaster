using Microsoft.UI;
using StorageMaster.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using StorageMaster.Core.Models;
using StorageMaster.Core.SpaceMap;
using StorageMaster.Core.Theming;
using StorageMaster.UI.Converters;
using Windows.UI;

namespace StorageMaster.UI.Controls;

public sealed class TreemapTileControl : Button
{
    /// <summary>
    /// Relative luminance at which a near-black label overtakes a white one for the
    /// WCAG ratio. <c>PaletteUsageContrastTests</c> holds the categorical ramp to
    /// this same crossover, so every fill the theme can produce stays labellable.
    /// </summary>
    private const double LuminanceCrossover = 0.18;

    private static readonly SolidColorBrush OnLightFillBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x0E, 0x11, 0x16));
    private static readonly SolidColorBrush OnDarkFillBrush = new(Colors.White);
    private static readonly SolidColorBrush UncategorisedFillBrush = new(Colors.Gray);
    private static readonly SolidColorBrush NoRingBrush = new(Colors.Transparent);

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

        // Categorical colours are tuned for small marks — legend swatches, thin
        // bars — where they need to be told apart at a glance. A treemap block is
        // hundreds of times that area, and at full strength it reads as a neon slab
        // that overpowers the panel it sits in. Blending toward the chart well keeps
        // the hue identifiable while letting size, not saturation, carry the signal.
        var fill = MuteForLargeArea(FillBrushFor(LayoutNode.Node));
        var label = LabelBrushFor(fill);

        Width = Math.Max(0, LayoutNode.Width - 3);
        Height = Math.Max(0, LayoutNode.Height - 3);
        Background = fill;
        // The selection ring is drawn in the tile's own label colour instead of a
        // fixed white, so it stays visible on a pale categorical fill in light theme.
        BorderBrush = IsSelected ? label : NoRingBrush;
        BorderThickness = new Thickness(IsSelected ? 3 : 1);
        ToolTipService.SetToolTip(this, $"{LayoutNode.Node.FullPath}\n{ByteSizeConverter.Format(LayoutNode.Node.SizeBytes)} ({LayoutNode.Node.PercentOfParent:N1}% of parent)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"{Loc.Get(EnumDisplayConverter.KeyFor(LayoutNode.Node.Kind))} {LayoutNode.Node.DisplayName}, {ByteSizeConverter.Format(LayoutNode.Node.SizeBytes)}");
        Content = BuildContent(LayoutNode, label);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Opacity = 0.92;
        BorderBrush = LabelBrushFor(Background);
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

    /// <summary>
    /// Builds the tile label. The label brush is assigned per TextBlock rather than
    /// inherited from the control: the Button template swaps its own foreground in the
    /// pointer-over and pressed visual states, which would drop the tile back to the
    /// theme foreground exactly while the user is looking at it.
    /// </summary>
    private static UIElement BuildContent(SpaceMapLayoutNode layout, Brush label)
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
            Foreground = label,
        });

        if (layout.Width >= 92 && layout.Height >= 52)
        {
            panel.Children.Add(new TextBlock
            {
                Text = ByteSizeConverter.Format(layout.Node.SizeBytes),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.78,
                FontSize = 12,
                Foreground = label,
            });
        }

        return panel;
    }

    /// <summary>
    /// Resolves the tile fill from the shared <c>FileType*Brush</c> resources — the
    /// very keys the Space Map legend paints its swatches from.
    /// <para>
    /// These colours used to be named <c>Colors.*</c> constants here while the legend
    /// carried its own hex values in XAML. The two sets drifted far enough that the
    /// legend showed videos as magenta while the map drew them red, so the key
    /// actively misled. Resolving one palette by key makes that drift impossible, and
    /// because <c>ThemeService</c> mutates those brushes in place the map follows a
    /// theme or accent change without being rebuilt.
    /// </para>
    /// </summary>
    private static Brush FillBrushFor(SpaceMapNode node) =>
        (Application.Current.Resources.TryGetValue(FillKeyFor(node), out var resource)
            ? resource as Brush
            : null)
        ?? UncategorisedFillBrush;

    private static string FillKeyFor(SpaceMapNode node) => node.Kind == SpaceMapNodeKind.Folder
        ? "FileTypeFolderBrush"
        : node.Category switch
        {
            FileTypeCategory.Image => "FileTypeImageBrush",
            FileTypeCategory.Video => "FileTypeVideoBrush",
            FileTypeCategory.Audio => "FileTypeAudioBrush",
            FileTypeCategory.Archive => "FileTypeArchiveBrush",
            FileTypeCategory.Executable or FileTypeCategory.Installer => "FileTypeExecutableBrush",
            FileTypeCategory.Document => "FileTypeDocumentBrush",
            FileTypeCategory.SourceCode => "FileTypeSourceCodeBrush",
            FileTypeCategory.Cache or FileTypeCategory.Temporary or FileTypeCategory.Log => "FileTypeCacheBrush",
            FileTypeCategory.SystemFile => "FileTypeSystemBrush",
            _ => "FileTypeOtherBrush",
        };

    /// <summary>
    /// Chooses the label colour for a given fill. The theme foreground is wrong here:
    /// the categorical ramp is light in dark theme and dark in light theme, so a
    /// single inherited foreground is unreadable on half the tiles either way. Picking
    /// near-black or white by the fill's relative luminance is the rule
    /// <c>PaletteUsageContrastTests</c> mirrors against the catalogue.
    /// </summary>
    /// <summary>
    /// Blends a categorical colour toward the chart well so it can fill a large
    /// area without dominating. The legend keeps the unblended colour, so the two
    /// still read as the same hue.
    /// </summary>
    private static Brush MuteForLargeArea(Brush fill)
    {
        if (fill is not SolidColorBrush solid)
            return fill;

        var well = ResolveWellColor();
        const double categoryWeight = 0.55;

        static byte Mix(byte category, byte well, double weight) =>
            (byte)Math.Clamp((category * weight) + (well * (1 - weight)), 0, 255);

        return new SolidColorBrush(Color.FromArgb(
            0xFF,
            Mix(solid.Color.R, well.R, categoryWeight),
            Mix(solid.Color.G, well.G, categoryWeight),
            Mix(solid.Color.B, well.B, categoryWeight)));
    }

    /// <summary>
    /// The surface a treemap sits on. Read from the live palette so blending
    /// follows the selected theme instead of assuming dark.
    /// </summary>
    private static Color ResolveWellColor()
    {
        if (Application.Current.Resources.TryGetValue("SmSurfaceSunkenBrush", out var value)
            && value is SolidColorBrush well)
        {
            return well.Color;
        }

        return Colors.Black;
    }

    internal static Brush LabelBrushFor(Brush? fill)
    {
        if (fill is not SolidColorBrush solid)
            return OnDarkFillBrush;

        var luminance = ColorContrast.RelativeLuminance(
            new Rgb(solid.Color.R, solid.Color.G, solid.Color.B));
        return luminance > LuminanceCrossover ? OnLightFillBrush : OnDarkFillBrush;
    }
}
