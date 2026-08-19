namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// One reviewable state that is not simply a page sitting idle.
/// </summary>
/// <param name="Id">File-name stem for the capture.</param>
/// <param name="TitleKey">Resource key for the dialog title.</param>
/// <param name="BodyKey">Resource key for the dialog body.</param>
/// <param name="PrimaryKey">Resource key for the confirming button.</param>
/// <param name="BodyArguments">
/// Stand-in values for the body's placeholders. Chosen to be plausible rather than
/// tidy — a count of 1 hides a plural bug and a small size hides a wrapping one.
/// </param>
public sealed record SafetyDialogScenario(
    string Id,
    string TitleKey,
    string BodyKey,
    string PrimaryKey,
    object[] BodyArguments);

/// <summary>
/// The scenarios <c>--capture-screens --scenarios</c> can reach.
/// <para>
/// Declared here rather than discovered because a confirmation dialog only exists
/// while someone is deleting something, and a capture harness must never be the
/// thing that starts a deletion. The dialogs are rebuilt from the same resource
/// keys the real call sites use, so the text reviewed is the text shipped.
/// </para>
/// <para>
/// That leaves a drift risk: a new safety dialog would not appear here and would
/// silently go unreviewed. <c>SafetyDialogCoverageTests</c> closes it by failing
/// when a <c>Safety_</c> dialog key is used in a view model but is missing from
/// this list.
/// </para>
/// </summary>
public static class ScenarioCatalogue
{
    /// <summary>
    /// The confirmations that stand between a user and losing data. These carry the
    /// wording that matters most in translation, and they are the states a review
    /// is least likely to reach by hand.
    /// </summary>
    public static IReadOnlyList<SafetyDialogScenario> SafetyDialogs { get; } =
    [
        new(
            Id: "dialog-duplicates-delete",
            TitleKey: "Safety_Duplicates_ConfirmDelete_Title",
            BodyKey: "Safety_Duplicates_ConfirmDelete_Body",
            PrimaryKey: "Safety_Duplicates_ConfirmDelete_Action",
            BodyArguments: ["1.284", "3", "SHA-256"]),

        new(
            Id: "dialog-smart-recyclebin",
            TitleKey: "Safety_Smart_Confirm_Title",
            BodyKey: "Safety_Smart_Confirm_RecycleBin_Message",
            PrimaryKey: "Safety_Smart_Confirm_MoveButton",
            BodyArguments: ["1.284", "12,4 GB"]),

        new(
            Id: "dialog-smart-permanent",
            TitleKey: "Safety_Smart_Confirm_Title",
            BodyKey: "Safety_Smart_Confirm_Permanent_Message",
            PrimaryKey: "Safety_Smart_Confirm_DeleteButton",
            BodyArguments: ["1.284", "12,4 GB"]),

        new(
            Id: "dialog-delete-scan",
            TitleKey: "Safety_DeleteScan_Title",
            BodyKey: "Safety_DeleteScan_Body",
            PrimaryKey: "Common_Delete",
            BodyArguments: [@"C:\", "19.08.2026 09:42"]),

        new(
            Id: "dialog-send-to-recyclebin",
            TitleKey: "Safety_SendToRecycleBin_Title",
            BodyKey: "Safety_SendToRecycleBin_Body",
            PrimaryKey: "Safety_SendToRecycleBin",
            BodyArguments: ["rootfs.vhdx", "7,70 GB"]),
    ];
}
