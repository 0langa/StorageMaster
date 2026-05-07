using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StorageMaster.UI.Controls;

public sealed partial class SettingsCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconGlyphProperty =
        DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(SettingsCard), new PropertyMetadata("\uE713"));

    public static readonly DependencyProperty BadgeTextProperty =
        DependencyProperty.Register(nameof(BadgeText), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty WarningTextProperty =
        DependencyProperty.Register(nameof(WarningText), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, OnWarningChanged));

    public SettingsCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public string BadgeText
    {
        get => (string)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public string WarningText
    {
        get => (string)GetValue(WarningTextProperty);
        set => SetValue(WarningTextProperty, value);
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    private static void OnWarningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsCard card)
            card.Bindings.Update();
    }
}
