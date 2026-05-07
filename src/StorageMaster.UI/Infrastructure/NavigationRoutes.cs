using StorageMaster.UI.Pages;

namespace StorageMaster.UI.Infrastructure;

internal static class NavigationRoutes
{
    internal const string Dashboard = "Dashboard";
    internal const string Scan = "Scan";
    internal const string Results = "Results";
    internal const string Duplicates = "Duplicates";
    internal const string Cleanup = "Cleanup";
    internal const string SmartCleaner = "SmartCleaner";
    internal const string SpaceMap = "SpaceMap";
    internal const string DriveHealth = "DriveHealth";
    internal const string Settings = "Settings";

    internal static readonly IReadOnlyDictionary<string, Type> TagToPage = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [Dashboard] = typeof(DashboardPage),
        [Scan] = typeof(ScanPage),
        [Results] = typeof(ResultsPage),
        [Duplicates] = typeof(DuplicatesPage),
        [Cleanup] = typeof(CleanupPage),
        [SmartCleaner] = typeof(SmartCleanerPage),
        [SpaceMap] = typeof(SpaceMapPage),
        [DriveHealth] = typeof(DriveHealthPage),
        [Settings] = typeof(SettingsPage),
    };

    internal static readonly IReadOnlyDictionary<Type, string> PageToTag = TagToPage
        .ToDictionary(static pair => pair.Value, static pair => pair.Key);
}
