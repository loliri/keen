using System.ComponentModel;
using System.Diagnostics;
using Keen.Models;
using Keen.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keen.Forms;

internal sealed class HistoryForm : Form
{
    private readonly Guid _guid;
    private readonly string _displayName;
    private readonly IServiceProvider _sp;
    private readonly VaultIndex _index;
    private readonly VaultStore _store;
    private readonly RestoreService _restore;

    private readonly DataGridView _grid = new();
    private readonly BindingList<VersionRow> _rows = new();
    private readonly Button _open = new() { Text = "打开此版本" };
    private readonly Button _folder = new() { Text = "打开所在文件夹" };
    private readonly Button _restoreBtn = new() { Text = "恢复…" };
    private readonly Button _export = new() { Text = "导出另存…" };
    private readonly Button _refresh = new() { Text = "刷新" };

    public HistoryForm(Guid guid, string displayName, string origPath, IServiceProvider sp)
    {
        _guid = guid; _displayName = displayName; _sp = sp;
        _index = sp.GetRequiredService<VaultIndex>();
        _store = sp.GetRequiredService<VaultStore>();
        _restore = sp.GetRequiredService<RestoreService>();

        Text = $"Keen · 版本历史 — {displayName}";
        Width = 840; Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        BuildUi();
        Load += async (_, _) => await LoadAsync();
    }

    private void BuildUi()
    {
        var top = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = $"  文件:{_displayName}",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 6, 8, 0),
            WrapContents = false,
        };
        foreach (var b in new[] { _open, _folder, _restoreBtn, _export, _refresh })
        {
            b.Margin = new Padding(0, 0, 8, 0);
            b.AutoSize = true;
            bar.Controls.Add(b);
        }

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        AddCol("时间", nameof(VersionRow.TimeDisplay), 160);
        AddCol("大小", nameof(VersionRow.SizeDisplay), 90);
        AddCol("类型", nameof(VersionRow.KindDisplay), 110);
        AddCol("较上一版", nameof(VersionRow.DeltaDisplay), 90);
        _grid.DataSource = _rows;

        Controls.Add(_grid);
        Controls.Add(bar);
        Controls.Add(top);

        _open.Click += (_, _) => OpenVer();
        _folder.Click += (_, _) => OpenFolder();
        _restoreBtn.Click += async (_, _) => await RestoreAsync();
        _export.Click += (_, _) => Export();
        _refresh.Click += async (_, _) => await LoadAsync();
    }

    private void AddCol(string header, string prop, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = prop,
            Name = prop,
            Width = width,
            FillWeight = width,
        });

    private async Task LoadAsync()
    {
        _rows.Clear();
        var vers = await _index.GetVersionsAsync(_guid); // DESC(最新在前)
        // 按时间升序算「较上一版」增量
        var asc = vers.OrderBy(v => v.CapturedAtTicks).ThenBy(v => v.Seq).ToList();
        var deltaMap = new Dictionary<long, long>();
        long prev = -1;
        foreach (var v in asc)
        {
            deltaMap[v.Id] = prev < 0 ? 0 : v.SizeBytes - prev;
            prev = v.SizeBytes;
        }
        foreach (var v in vers) _rows.Add(new VersionRow { Entry = v, DeltaBytes = deltaMap[v.Id] });
    }

    private VersionEntry? Selected() => (_grid.CurrentRow?.DataBoundItem as VersionRow)?.Entry;

    private void OpenVer()
    {
        if (Selected() is not { } e) return;
        try { Process.Start(new ProcessStartInfo(_store.FullPath(e.StoredRelPath)) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打开失败:" + ex.Message, "Keen"); }
    }

    private void OpenFolder()
    {
        if (Selected() is not { } e) return;
        var path = _store.FullPath(e.StoredRelPath);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打开失败:" + ex.Message, "Keen"); }
    }

    private async Task RestoreAsync()
    {
        if (Selected() is not { } e) return;
        var ok = MessageBox.Show(this,
            "恢复会把所选历史版本覆盖回原文件。\n\n当前的原文件会先自动存进历史(作为「恢复前快照」),\n所以这一步可以再撤销。\n\n确定恢复?",
            "Keen · 恢复", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return;

        UseWaitCursor = true;
        _restoreBtn.Enabled = false;
        try
        {
            await _restore.RestoreAsync(e);
            MessageBox.Show(this, "已恢复。原文件已回到所选版本;恢复前的状态已存为「恢复前快照」。",
                "Keen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "恢复失败:" + ex.Message, "Keen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _restoreBtn.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private void Export()
    {
        if (Selected() is not { } e) return;
        using var sfd = new SaveFileDialog { FileName = e.OrigFilename, Title = "导出此版本到…" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try { File.Copy(_store.FullPath(e.StoredRelPath), sfd.FileName, overwrite: true); }
        catch (Exception ex) { MessageBox.Show(this, "导出失败:" + ex.Message, "Keen"); }
    }
}

internal sealed class VersionRow
{
    public VersionEntry Entry { get; set; } = new();
    public long DeltaBytes { get; set; }

    public string TimeDisplay =>
        new DateTime(Entry.CapturedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeDisplay => FormatBytes(Entry.SizeBytes);
    public string KindDisplay => Entry.Kind switch
    {
        VersionKind.Normal => "常规",
        VersionKind.PreRestoreSnapshot => "恢复前快照",
        VersionKind.SyntheticRestore => "恢复",
        _ => Entry.Kind.ToString(),
    };
    public string DeltaDisplay => DeltaBytes == 0 ? "—" :
        (DeltaBytes > 0 ? "+" : "") + FormatBytes(DeltaBytes);

    internal static string FormatBytes(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1024L * 1024) return (n / 1024.0).ToString("F1") + " KB";
        if (n < 1024L * 1024 * 1024) return (n / 1024.0 / 1024).ToString("F1") + " MB";
        return (n / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
    }
}
