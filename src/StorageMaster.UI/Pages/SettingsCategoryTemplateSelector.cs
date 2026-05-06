using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StorageMaster.UI.Pages;

public sealed class SettingsCategoryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GeneralTemplate { get; set; }
    public DataTemplate? ScanningTemplate { get; set; }
    public DataTemplate? CleanupTemplate { get; set; }
    public DataTemplate? DuplicatesTemplate { get; set; }
    public DataTemplate? ResultsHistoryTemplate { get; set; }
    public DataTemplate? SchedulingTemplate { get; set; }
    public DataTemplate? TrayNotificationsTemplate { get; set; }
    public DataTemplate? UpdatesTemplate { get; set; }
    public DataTemplate? AdvancedDiagnosticsTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is SettingsViewModel vm)
        {
            var template = vm.SelectedCategory switch
            {
                SettingsCategory.General => GeneralTemplate,
                SettingsCategory.Scanning => ScanningTemplate,
                SettingsCategory.Cleanup => CleanupTemplate,
                SettingsCategory.Duplicates => DuplicatesTemplate,
                SettingsCategory.ResultsHistory => ResultsHistoryTemplate,
                SettingsCategory.Scheduling => SchedulingTemplate,
                SettingsCategory.TrayNotifications => TrayNotificationsTemplate,
                SettingsCategory.Updates => UpdatesTemplate,
                SettingsCategory.AdvancedDiagnostics => AdvancedDiagnosticsTemplate,
                _ => GeneralTemplate,
            };
            return template ?? base.SelectTemplateCore(item, container);
        }

        return base.SelectTemplateCore(item, container);
    }
}
