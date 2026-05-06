using CommunityToolkit.Mvvm.ComponentModel;

namespace StorageMaster.UI.Pages;

public sealed partial class SettingsCategoryItem : ObservableObject
{
    public SettingsCategory Category { get; }
    public string Title { get; }
    public string Description { get; }
    public string IconGlyph { get; }

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private bool _hasWarning;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    public SettingsCategoryItem(SettingsCategory category, string title, string description, string iconGlyph)
    {
        Category = category;
        Title = title;
        Description = description;
        IconGlyph = iconGlyph;
    }
}
