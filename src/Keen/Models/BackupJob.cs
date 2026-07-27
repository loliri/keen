namespace Keen.Models;

internal sealed class BackupJob
{
    public Guid WatchedGuid { get; init; }
    public string SourcePath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public VersionKind Kind { get; init; } = VersionKind.Normal;
    // PreRestoreSnapshot 等必须留痕的作业绕过去重(即便字节与上次相同)。
    public bool BypassDedupe { get; init; }
}
