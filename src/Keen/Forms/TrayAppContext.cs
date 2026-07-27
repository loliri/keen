using Keen.Models;
using Keen.Services;
using Keen.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Keen.Forms;

// 托盘优先:启动时不建主窗,只挂 NotifyIcon。
// 启动时载入 DB 中的被监控文件、武装管线与 watcher;退出时干净停止(不变量⑤:DB 权威,关停有序)。
// 托盘图标随聚合健康状态切换(任一 Failing/Degraded/Missing → error 图标)。
internal sealed class TrayAppContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Keen";

    private readonly ServiceProvider _services;
    private readonly NotifyIcon _tray;
    private readonly Form _invoker;            // 隐藏,仅作 UI 线程封送目标
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<Guid, FileHealth> _health = new();
    private readonly ToolStripMenuItem _autoItem;

    public TrayAppContext(ServiceProvider services)
    {
        _services = services;
        _invoker = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(0, 0) };
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _autoItem = new ToolStripMenuItem("开机自启") { Checked = IsAutostartOn() };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗(&S)", image: null, (_, _) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 Keen(&X)", image: null, (_, _) => ExitApp());
        _autoItem.Click += (_, _) =>
        {
            var wantOn = !_autoItem.Checked;
            SetAutostart(wantOn);
            _autoItem.Checked = IsAutostartOn();
        };

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Idle,
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

        pipeline.HealthChanged += OnHealth;
        var files = await index.LoadActiveWatchedFilesAsync();
        foreach (var wf in files)
        {
            await pipeline.RegisterAsync(wf.Guid, wf.CurrentPath, wf.DisplayName);
            watcher.Add(wf);
        }
    }

    private void OnHealth(Guid guid, FileHealth h, string? msg) =>
        _ui.Post(_ =>
        {
            _health[guid] = h;
            var anyBad = _health.Values.Any(x =>
                x == FileHealth.Failing || x == FileHealth.Degraded || x == FileHealth.Missing);
            _tray.Icon = anyBad ? TrayIcon.Error : TrayIcon.Idle;
        }, null);

    private void ShowMain()
    {
        var main = _services.GetRequiredService<MainForm>();
        if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
        if (!main.Visible) main.Show();
        main.BringToFront();
        main.Activate();
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        try
        {
            var main = _services.GetService<MainForm>();
            if (main is not null) { main.AllowClose = true; main.Close(); }
        }
        catch { }
        try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        ExitThread();
    }

    private async Task ShutdownAsync()
    {
        var pipeline = _services.GetRequiredService<BackupPipeline>();
        var watcher = _services.GetRequiredService<FileWatchService>();
        var index = _services.GetRequiredService<VaultIndex>();
        pipeline.HealthChanged -= OnHealth;
        try { await pipeline.StopAsync(); } catch { }
        watcher.Dispose();
        index.Dispose();
    }

    private static bool IsAutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppName) is string s && !string.IsNullOrEmpty(s);
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(AppName, Application.ExecutablePath);
        else k.DeleteValue(AppName, throwOnMissingValue: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _invoker.Dispose();
            _services.Dispose();
        }
        base.Dispose(disposing);
    }
}
