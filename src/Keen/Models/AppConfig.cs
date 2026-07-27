namespace Keen.Models;

// 薄引导配置;真正的权威是 SQLite 保险库索引(M2)。这里只存启动引导所需的最低信息。
internal sealed class AppConfig
{
    public string VaultRoot { get; set; } = "";
    public List<string> WatchedFiles { get; set; } = new();
    public bool SkipIdentical { get; set; } = false;
    public bool Autostart { get; set; } = false;
}
