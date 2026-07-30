using Keen.Models;
using Microsoft.Extensions.Logging;

namespace Keen.Services;

// 集中被监控文件的增删 + 启动武装。主窗、IPC、恢复都走这里,避免逻辑重复。
// 「移除」= 软删除(is_active=0):停止监控,但 watched_file 行与全部 version 历史(含 blob)保留。
// 「彻底删除」= Purge:删历史行 + blob + watched_file 行。
internal sealed class WatchService
{
    private readonly VaultIndex _index;
    private readonly BackupPipeline _pipeline;
    private readonly FileWatchService _watcher;
    private readonly ConfigService _config;
    private readonly VaultStore _store;
    private readonly ILogger<WatchService> _log;

    public WatchService(VaultIndex index, BackupPipeline pipeline, FileWatchService watcher,
        ConfigService config, VaultStore store, ILogger<WatchService> log)
    {
        _index = index; _pipeline = pipeline; _watcher = watcher; _config = config; _store = store; _log = log;
    }

    public event Action<WatchedFile>? FileAdded;
    public event Action<WatchedFile>? FileReactivated;
    public event Action<Guid>? FileDeactivated; // 软删除(留历史)
    public event Action<Guid>? FileRemoved;     // 彻底删除(清历史)

    public async Task InitializeAsync()
    {
        var files = await _index.LoadActiveWatchedFilesAsync();
        foreach (var wf in files)
        {
            await _pipeline.RegisterAsync(wf.Guid, wf.CurrentPath, wf.DisplayName);
            _watcher.Add(wf);
        }
        RestoreService.CleanupOrphanRestoreTemps(files); // 清上次崩溃在各原文件目录残留的 .keenrestore.*.tmp
    }

    public async Task<WatchedFile?> AddFileAsync(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var vaultRoot = Path.GetFullPath(_config.Current.VaultRoot);
        if (full.StartsWith(vaultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, vaultRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (Directory.Exists(full)) return null;

        // 已存在(含已软删除的):直接重新激活,复用原历史
        var existing = await _index.LoadAllWatchedFilesAsync();
        var hit = existing.FirstOrDefault(w => string.Equals(w.CurrentPath, full, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            if (!hit.IsActive)
            {
                await _index.ReactivateWatchedFileAsync(hit.Guid);
                await _pipeline.RegisterAsync(hit.Guid, hit.CurrentPath, hit.DisplayName);
                _watcher.Add(hit);
                hit.IsActive = true;
                FileReactivated?.Invoke(hit);
            }
            return hit;
        }

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
        // 添加即存第一版(当前状态作为基线),否则要等下次保存才有版本。
        _pipeline.Enqueue(new BackupJob { WatchedGuid = wf.Guid, SourcePath = wf.CurrentPath, DisplayName = wf.DisplayName });
        FileAdded?.Invoke(wf);
        _log.LogInformation("已添加监控:{Path}", full);
        return wf;
    }

    // 软删除:停监控,留历史。
    public async Task RemoveAsync(Guid guid)
    {
        await _index.DeactivateWatchedFileAsync(guid);
        _pipeline.Unregister(guid);
        _watcher.Remove(guid);
        FileDeactivated?.Invoke(guid);
    }

    // 重新监控(复用原 GUID + 历史)。
    public async Task<bool> ReactivateAsync(Guid guid)
    {
        var wf = await _index.GetWatchedFileAsync(guid);
        if (wf is null) return false;
        await _index.ReactivateWatchedFileAsync(guid);
        await _pipeline.RegisterAsync(guid, wf.CurrentPath, wf.DisplayName);
        _watcher.Add(wf);
        wf.IsActive = true;
        FileReactivated?.Invoke(wf);
        return true;
    }

    // 彻底删除:历史行 + blob + watched_file 行。
    public async Task PurgeAsync(Guid guid)
    {
        _pipeline.Unregister(guid);
        _watcher.Remove(guid);
        var rels = await _index.PurgeWatchedFileAsync(guid);
        foreach (var rel in rels) _store.DeleteBlob(rel);
        FileRemoved?.Invoke(guid);
    }
}
