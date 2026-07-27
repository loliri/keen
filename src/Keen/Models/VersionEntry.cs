namespace Keen.Models;

internal sealed class VersionEntry
{
    public long Id { get; set; }
    public Guid WatchedGuid { get; set; }
    public long CapturedAtTicks { get; set; }
    public int Seq { get; set; }
    public VersionKind Kind { get; set; }
    public string StoredRelPath { get; set; } = "";
    public string OrigPathAtCapture { get; set; } = "";
    public string OrigFilename { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? Note { get; set; }
}
