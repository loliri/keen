using Keen.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keen.Forms;

// 托盘优先:启动时不建主窗,在 ApplicationContext 里只挂 NotifyIcon。
// 启动时载入 DB 中的被监控文件、武装管线与 watcher;退出时干净停止(不变量⑤:DB 权威,关停有序)。
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ServiceProvider _services;

    public TrayAppContext(ServiceProvider services)
    {
        _services = services;

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗(&S)", image: null, (_, _) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 Keen(&X)", image: null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application, // 占位;M5 换刻痕 motif
            Text = "Keen (刻痕)",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    public async Task StartupAsync()
    {
        var pipeline = _services.GetRequiredService<BackupPipeline>();
        var watcher = _services.GetRequiredService<FileWatchService>();
        var index = _services.GetRequiredService<VaultIndex>();

        var files = await index.LoadActiveWatchedFilesAsync();
        foreach (var wf in files)
        {
            await pipeline.RegisterAsync(wf.Guid, wf.CurrentPath, wf.DisplayName);
            watcher.Add(wf);
        }
    }

    private void ShowMain()
    {
        // M3 接入 MainForm;M2 暂占位。
        MessageBox.Show($"Keen 核心已就绪。当前监听由 DB 已有的被监控文件驱动(主窗 M3 实现后可增删)。",
            "Keen", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        ExitThread();
    }

    private async Task ShutdownAsync()
    {
        var pipeline = _services.GetRequiredService<BackupPipeline>();
        var watcher = _services.GetRequiredService<FileWatchService>();
        var index = _services.GetRequiredService<VaultIndex>();
        try { await pipeline.StopAsync(); } catch { }
        watcher.Dispose();
        index.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _services.Dispose();
        }
        base.Dispose(disposing);
    }
}
