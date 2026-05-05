namespace StorageMaster.Core.Models;

public sealed record DuplicatePreviewItem
{
    public string Path { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string PreviewPath { get; init; } = string.Empty;
}

public sealed record DuplicatePreviewResult
{
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<DuplicatePreviewItem> Items { get; init; } = [];
}
