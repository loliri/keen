using Keen.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace Keen.Services;

// 文件监听(不变量⑦):每个不同父目录一个 FileSystemWatcher(复数 Filters);Changed/Created/Renamed →
// 管线防抖。Error → 1s 退避重建 + catch-up。60s 健康轮询查父目录是否还在(改名无事件、无 OnError)。
// 睡眠/唤醒(无 Error)经 PowerModeChanged(Resume)重建 + 全量 catch-up。
internal sealed class FileWatchService : IDisposable
{
    private readonly BackupPipeline _pipeline;
    private readonly ILogger<FileWatchService> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DirWatch> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, WatchedFile> _files = new();
    private readonly Timer _healthPoll;

    public FileWatchService(BackupPipeline pipeline, ILogger<FileWatchService> log)
    {
        _pipeline = pipeline;
        _log = log;
        _healthPoll = new Timer(_ => HealthPoll(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        try { SystemEvents.PowerModeChanged += OnPowerModeChanged; }
        catch (Exception ex) { _log.LogWarning(ex, "无法挂载电源事件钩子(睡眠/唤醒恢复将退化为下次轮询)"); }
    }

    private sealed class DirWatch
    {
        public FileSystemWatcher Fsw = null!;
        public Dictionary<string, Guid> NameToGuid = new(StringComparer.OrdinalIgnoreCase);
    }

    public void Add(WatchedFile wf)
    {
        lock (_gate)
        {
            _files[wf.Guid] = wf;
            EnsureDir(Path.GetDirectoryName(wf.CurrentPath) ?? "");
        }
    }

    public void Remove(Guid guid)
    {
        string? dir;
        lock (_gate)
        {
            if (!_files.TryGetValue(guid, out var wf)) return;
            dir = Path.GetDirectoryName(wf.CurrentPath) ?? "";
            _files.Remove(guid);
            if (_dirs.ContainsKey(dir)) EnsureDir(dir);
        }
        _pipeline.MarkHealth(guid, FileHealth.Watching, null);
    }

    // 按 _files 当前状态重建某目录的 watcher;无文件则释放。
    private void EnsureDir(string dir)
    {
        if (_dirs.TryGetValue(dir, out var old)) { try { old.Fsw.Dispose(); } catch { } _dirs.Remove(dir); }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameToGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var wf in _files.Values)
        {
            var d = Path.GetDirectoryName(wf.CurrentPath) ?? "";
            if (!dir.Equals(d, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(wf.CurrentPath);
            names.Add(name);
            nameToGuid[name] = wf.Guid;
        }
        if (names.Count == 0) return;

        var fsw = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 32 * 1024,
            IncludeSubdirectories = false,
        };
        foreach (var n in names) fsw.Filters.Add(n);
        fsw.Changed += (_, e) => Route(e.FullPath);
        fsw.Created += (_, e) => Route(e.FullPath);
        fsw.Renamed += (_, e) => Route(e.FullPath);
        fsw.Error += OnError;
        fsw.EnableRaisingEvents = true;
        _dirs[dir] = new DirWatch { Fsw = fsw, NameToGuid = nameToGuid };
    }

    private void Route(string fullPath)
    {
        Guid guid;
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(fullPath) ?? "";
            var name = Path.GetFileName(fullPath);
            if (!_dirs.TryGetValue(dir, out var dw)) return;
            if (!dw.NameToGuid.TryGetValue(name, out guid)) return;
        }
        _pipeline.OnFileChanged(guid);
    }

    private async void OnError(object? sender, ErrorEventArgs e)
    {
        try
        {
            var fsw = (FileSystemWatcher)sender!;
            var dir = fsw.Path;
            _log.LogWarning(e.GetException(), "FSW error on {Dir}, 1s 后重建 + catch-up", dir);
            try { fsw.Dispose(); } catch { }
            await Task.Delay(1000);
            List<WatchedFile> snap;
            lock (_gate) { EnsureDir(dir); snap = FilesInDir(dir); }
            foreach (var wf in snap) _pipeline.EnqueueCatchup(wf.Guid, wf.CurrentPath, wf.DisplayName);
        }
        catch (Exception ex) { _log.LogError(ex, "OnError 处理出错"); }
    }

    private void HealthPoll()
    {
        List<(Guid guid, bool missing)> changes = new();
        lock (_gate)
        {
            foreach (var wf in _files.Values)
            {
                var dir = Path.GetDirectoryName(wf.CurrentPath) ?? "";
                if (!Directory.Exists(dir))
                {
                    changes.Add((wf.Guid, true));
                    if (_dirs.ContainsKey(dir)) { try { _dirs[dir].Fsw.Dispose(); } catch { } _dirs.Remove(dir); }
                }
            }
        }
        foreach (var (guid, _) in changes)
            _pipeline.MarkHealth(guid, FileHealth.Missing, "父目录不存在(被改名/移动?)");
    }

    private async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        try
        {
            _log.LogInformation("电源恢复,重建监听 + 全量 catch-up");
            List<WatchedFile> snap;
            lock (_gate)
            {
                foreach (var d in _dirs.Values) { try { d.Fsw.Dispose(); } catch { } }
                _dirs.Clear();
                foreach (var wf in _files.Values) EnsureDir(Path.GetDirectoryName(wf.CurrentPath) ?? "");
                snap = _files.Values.ToList();
            }
            foreach (var wf in snap) _pipeline.EnqueueCatchup(wf.Guid, wf.CurrentPath, wf.DisplayName);
        }
        catch (Exception ex) { _log.LogError(ex, "电源恢复处理出错"); }
    }

    private List<WatchedFile> FilesInDir(string dir) =>
        _files.Values.Where(w => (Path.GetDirectoryName(w.CurrentPath) ?? "")
            .Equals(dir, StringComparison.OrdinalIgnoreCase)).ToList();

    public void Dispose()
    {
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        _healthPoll.Dispose();
        lock (_gate) { foreach (var d in _dirs.Values) { try { d.Fsw.Dispose(); } catch { } } _dirs.Clear(); }
    }
}
