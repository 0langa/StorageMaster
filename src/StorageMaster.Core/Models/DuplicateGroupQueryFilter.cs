namespace StorageMaster.Core.Models;

public sealed record DuplicateGroupQueryFilter
{
    public string SearchText { get; init; } = string.Empty;
    public DuplicateMethod? Method { get; init; }
    public double? MinConfidence { get; init; }
    public bool? HasSelectedMembers { get; init; }
    public bool? ExistsNow { get; init; }
    public bool IncludeErroredOnly { get; init; }
}
