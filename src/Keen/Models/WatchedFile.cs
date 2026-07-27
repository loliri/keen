namespace Keen.Models;

internal sealed class WatchedFile
{
    public Guid Guid { get; set; }
    public string CurrentPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long AddedAtTicks { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Note { get; set; }
}
