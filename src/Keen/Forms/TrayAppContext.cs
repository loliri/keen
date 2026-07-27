using Keen.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keen.Forms;

// 托盘优先:启动时不建主窗,在 ApplicationContext 里只挂 NotifyIcon。主窗(M3)按需 Show。
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
            Icon = SystemIcons.Application, // 占位图标;M5 换刻痕 motif
            Text = "Keen (刻痕)",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void ShowMain()
    {
        // M3 接入 MainForm;M1 先占位。
        MessageBox.Show("Keen 主窗(M3 实现)。", "Keen", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        ExitThread();
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
