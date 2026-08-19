using FluentAssertions;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scheduling;

namespace StorageMaster.Tests.Localization;

/// <summary>
/// Every enum bound to a settings drop-down must have a display string for every
/// member.
/// <para>
/// The drop-downs bind to <c>Enum.GetValues</c>, so a member with no key renders
/// as its own identifier — "CleanupExecuteSafe" — in English as well as in German
/// and Spanish. Nothing else catches that: the key is composed at runtime from the
/// type and member name, so <c>LocalizationScopeTests</c>, which scans for literal
/// key strings in source, cannot see it. Adding an enum member without a string is
/// the failure this exists to stop.
/// </para>
/// </summary>
public sealed class EnumDisplayTests
{
    /// <summary>
    /// The enums the Settings page binds a drop-down to. <c>DayOfWeek</c> is
    /// deliberately absent: weekday names come from the culture through .NET, so
    /// they are correct without the app shipping strings for them.
    /// </summary>
    public static TheoryData<Type> BoundEnums =>
    [
        typeof(ThemePreference),
        typeof(UiLanguage),
        typeof(UiDensity),
        typeof(KeeperPolicy),
        typeof(ScheduledJobKind),
        typeof(ScheduledJobFrequency),
        typeof(DuplicateGroupSortBy),
        typeof(DuplicateScopeMode),

        // Not a drop-down: the drive-health badge falls back to this enum for its
        // caption, which is the same leak by a different route.
        typeof(DriveHealthStatus),
    ];

    [Theory]
    [MemberData(nameof(BoundEnums))]
    public void EveryMemberHasADisplayStringInEveryLanguage(Type enumType)
    {
        foreach (var language in LocalizationCatalog.ShippedLanguages)
        {
            var strings = LocalizationCatalog.Strings(language);

            foreach (var value in Enum.GetValues(enumType))
            {
                var key = $"Enum_{enumType.Name}_{value}";

                strings.Should().ContainKey(key,
                    "{0}.{1} appears in a drop-down and would otherwise render as its own "
                    + "identifier in {2}", enumType.Name, value, language);

                strings[key].Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    /// <summary>
    /// Catches a display string that is really still the identifier.
    /// <para>
    /// Only multi-word members are checked. A one-word member like
    /// <c>Light</c> or <c>Daily</c> is legitimately the same in English as in code
    /// — the identifier happens to be the word. It is the run-together ones that
    /// give the leak away, and those are the ones a user actually sees as wrong.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(BoundEnums))]
    public void NoDisplayStringIsStillAnIdentifier(Type enumType)
    {
        foreach (var value in Enum.GetValues(enumType))
        {
            var name = value.ToString()!;

            // PascalCase with more than one word: an internal capital.
            if (!name.Skip(1).Any(char.IsUpper))
                continue;

            var english = LocalizationCatalog.Strings(LocalizationCatalog.English)[$"Enum_{enumType.Name}_{value}"];

            english.Should().NotBe(name,
                "'{0}' is the enum identifier rather than something to show a user. Even in "
                + "English a drop-down should read 'Run safe cleanup', not 'CleanupExecuteSafe'",
                name);
        }
    }
}
