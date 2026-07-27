using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Keen.Models;
using Keen.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keen.Forms;

internal sealed class MainForm : Form
{
    private readonly BackupPipeline _pipeline;
    private readonly FileWatchService _watcher;
    private readonly VaultIndex _index;
    private readonly ConfigService _config;
    private readonly IServiceProvider _sp;

    private readonly DataGridView _grid = new();
    private readonly BindingList<WatchedRow> _rows = new();
    private readonly Button _add = new() { Text = "添加文件…" };
    private readonly Button _remove = new() { Text = "移除" };
    private readonly Button _open = new() { Text = "打开" };
    private readonly Button _history = new() { Text = "历史…" };
    private readonly Button _pause = new() { Text = "暂停/恢复" };

    public bool AllowClose;

    public MainForm(IServiceProvider sp)
    {
        _sp = sp;
        _pipeline = sp.GetRequiredService<BackupPipeline>();
        _watcher = sp.GetRequiredService<FileWatchService>();
        _index = sp.GetRequiredService<VaultIndex>();
        _config = sp.GetRequiredService<ConfigService>();

        Text = "Keen · 被监控文件";
        Width = 920; Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;

        BuildUi();

        _pipeline.VersionCaptured += OnVersionCaptured;
        _pipeline.HealthChanged += OnHealthChanged;

        Load += async (_, _) => await LoadRowsAsync();
    }

    private void BuildUi()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 6, 8, 0),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        foreach (var b in new[] { _add, _remove, _open, _history, _pause })
        {
            b.Margin = new Padding(0, 0, 8, 0);
            b.AutoSize = true;
            bar.Controls.Add(b);
        }

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        AddCol("文件", nameof(WatchedRow.DisplayName), 160);
        AddCol("路径", nameof(WatchedRow.Path), 300);
        AddCol("状态", nameof(WatchedRow.HealthDisplay), 70);
        AddCol("上次存版", nameof(WatchedRow.LastCapturedDisplay), 150);
        AddCol("版本数", nameof(WatchedRow.VersionCount), 60);
        AddCol("大小", nameof(WatchedRow.SizeDisplay), 80);
        _grid.DataSource = _rows;

        // 顺序:Fill 控件先加(在顶部 dock 下方)。
        Controls.Add(_grid);
        Controls.Add(bar);

        _add.Click += async (_, _) => await AddAsync();
        _remove.Click += (_, _) => Remove();
        _open.Click += (_, _) => OpenSelected();
        _history.Click += (_, _) => HistorySelected();
        _pause.Click += (_, _) => TogglePause();
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

    private async Task LoadRowsAsync()
    {
        var files = await _index.LoadActiveWatchedFilesAsync();
        foreach (var wf in files)
        {
            var count = await _index.CountVersionsAsync(wf.Guid);
            var last = await _index.GetLastVersionAsync(wf.Guid);
            _rows.Add(new WatchedRow
            {
                Guid = wf.Guid,
                DisplayName = wf.DisplayName,
                Path = wf.CurrentPath,
                VersionCountField = count,
                LastCapturedTicksField = last?.CapturedAtTicks ?? 0,
                SizeBytesField = last?.SizeBytes ?? 0,
                HealthField = FileHealth.Watching,
            });
        }
    }

    private void OnVersionCaptured(VersionEntry e) =>
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            var row = FindRow(e.WatchedGuid);
            if (row is null) return;
            row.VersionCountField += 1;
            row.LastCapturedTicksField = e.CapturedAtTicks;
            row.SizeBytesField = e.SizeBytes;
            row.HealthField = FileHealth.Watching;
        });

    private void OnHealthChanged(Guid guid, FileHealth h, string? msg) =>
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            var row = FindRow(guid);
            if (row is not null) row.HealthField = h;
        });

    private WatchedRow? FindRow(Guid g) => _rows.FirstOrDefault(r => r.Guid == g);
    private WatchedRow? FindRowByPath(string p) =>
        _rows.FirstOrDefault(r => string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase));

    private async Task AddAsync()
    {
        using var ofd = new OpenFileDialog { Multiselect = true, Title = "选择要监控的文件" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        var vaultRoot = Path.GetFullPath(_config.Current.VaultRoot);
        foreach (var path in ofd.FileNames)
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                MessageBox.Show(this, $"不支持网络/UNC 路径:\n{full}", "Keen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            if (full.StartsWith(vaultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, vaultRoot, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"不能监控保险库内部的文件:\n{full}", "Keen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            if (FindRowByPath(full) is not null) continue;

            var wf = new WatchedFile
            {
                Guid = Guid.CreateVersion7(),
                CurrentPath = full,
                DisplayName = Path.GetFileName(full),
                AddedAtTicks = DateTime.UtcNow.Ticks,
                IsActive = true,
            };
            await _index.AddWatchedFileAsync(wf);
            await _pipeline.RegisterAsync(wf.Guid, wf.CurrentPath, wf.DisplayName);
            _watcher.Add(wf);
            _rows.Add(new WatchedRow
            {
                Guid = wf.Guid,
                DisplayName = wf.DisplayName,
                Path = wf.CurrentPath,
                HealthField = FileHealth.Watching,
            });
        }
    }

    private void Remove()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row) return;
        var ok = MessageBox.Show(this,
            $"移除对该文件的监控?(历史版本保留)\n{row.Path}", "Keen",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return;
        _ = _index.DeactivateWatchedFileAsync(row.Guid);
        _pipeline.Unregister(row.Guid);
        _watcher.Remove(row.Guid);
        _rows.Remove(row);
    }

    private void OpenSelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row) return;
        try { Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打开失败:" + ex.Message, "Keen"); }
    }

    private void HistorySelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row) return;
        new HistoryForm(row.Guid, row.DisplayName, row.Path, _sp) { ShowInTaskbar = false }.Show(this);
    }

    private void TogglePause()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row) return;
        if (row.IsPaused) { _watcher.Resume(row.Guid); row.IsPaused = false; }
        else { _watcher.Pause(row.Guid); row.IsPaused = true; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _pipeline.VersionCaptured -= OnVersionCaptured;
        _pipeline.HealthChanged -= OnHealthChanged;
        base.OnFormClosing(e);
    }
}

internal sealed class WatchedRow : INotifyPropertyChanged
{
    public Guid Guid { get; set; }
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";

    private long _versionCount;
    private long _lastTicks;
    private long _size;
    private FileHealth _health = FileHealth.Watching;

    public long VersionCount
    {
        get => _versionCount;
        set { if (_versionCount != value) { _versionCount = value; On(); } }
    }
    public long LastCapturedTicks
    {
        get => _lastTicks;
        set { if (_lastTicks != value) { _lastTicks = value; On(nameof(LastCapturedDisplay)); } }
    }
    public long SizeBytes
    {
        get => _size;
        set { if (_size != value) { _size = value; On(nameof(SizeDisplay)); } }
    }
    public FileHealth Health
    {
        get => _health;
        set { if (_health != value) { _health = value; On(nameof(HealthDisplay)); } }
    }

    // 让 MainForm 直接设底层字段而只触发一次通知(批量加载用)。
    internal long VersionCountField { get => _versionCount; set { _versionCount = value; On(nameof(VersionCount)); } }
    internal long LastCapturedTicksField { get => _lastTicks; set { _lastTicks = value; On(nameof(LastCapturedDisplay)); } }
    internal long SizeBytesField { get => _size; set { _size = value; On(nameof(SizeDisplay)); } }
    internal FileHealth HealthField { get => _health; set { _health = value; On(nameof(HealthDisplay)); } }

    public bool IsPaused
    {
        get => _isPaused;
        set { if (_isPaused != value) { _isPaused = value; On(nameof(HealthDisplay)); } }
    }
    private bool _isPaused;

    public string HealthDisplay => IsPaused ? "已暂停" : Health switch
    {
        FileHealth.Watching => "监听中",
        FileHealth.Syncing => "存版中",
        FileHealth.Degraded => "降级",
        FileHealth.Failing => "失败",
        FileHealth.Missing => "缺失",
        FileHealth.Paused => "已暂停",
        _ => Health.ToString(),
    };

    public string LastCapturedDisplay => LastCapturedTicks == 0 ? "—" :
        new DateTime(LastCapturedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string SizeDisplay => FormatBytes(SizeBytes);

    private static string FormatBytes(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1024L * 1024) return (n / 1024.0).ToString("F1") + " KB";
        if (n < 1024L * 1024 * 1024) return (n / 1024.0 / 1024).ToString("F1") + " MB";
        return (n / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
