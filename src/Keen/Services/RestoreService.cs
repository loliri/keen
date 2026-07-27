using Keen.Models;
using Microsoft.Extensions.Logging;

namespace Keen.Services;

// 可逆恢复(不变量⑧)。
// ① 先把「当前原文件」捕进库(kind=PreRestoreSnapshot,bypass 去重)——保证恢复前的字节永远在历史里。
// ② 把历史版本写到同目录 temp(WriteThrough + flush-to-disk),再 File.Move(overwrite) 覆盖原文件
//    (File.Move 仅元数据原子,WriteThrough+Flush 让数据崩溃可持久)。覆盖带 SHARING_VIOLATION 重试。
// ③ 覆盖会触发 watcher → 管线自动把恢复后的内容作为 Normal 版本入库(恢复结果留痕)。
internal sealed class RestoreService
{
    private readonly VaultStore _store;
    private readonly VaultIndex _index;
    private readonly BackupPipeline _pipeline;
    private readonly ILogger<RestoreService> _log;

    public RestoreService(VaultStore store, VaultIndex index, BackupPipeline pipeline, ILogger<RestoreService> log)
    {
        _store = store; _index = index; _pipeline = pipeline; _log = log;
    }

    public async Task RestoreAsync(VersionEntry target, CancellationToken ct = default)
    {
        var origPath = target.OrigPathAtCapture;
        if (!File.Exists(origPath))
            throw new FileNotFoundException("原文件当前不存在(可能被删除或移动):", origPath);
        var verPath = _store.FullPath(target.StoredRelPath);
        if (!File.Exists(verPath))
            throw new FileNotFoundException("历史版本文件缺失:", target.StoredRelPath);

        // ① 前置快照(bypass 去重,同步等待落地)
        var snap = await _pipeline.CaptureDirectAsync(target.WatchedGuid, origPath, target.OrigFilename,
            VersionKind.PreRestoreSnapshot, bypassDedupe: true, ct);
        if (snap is null)
            throw new InvalidOperationException("无法为恢复创建前置快照(捕获失败,请查看日志)。");

        // ② 写同目录 temp + 覆盖
        var dir = Path.GetDirectoryName(origPath);
        if (string.IsNullOrEmpty(dir)) throw new IOException("无法确定原文件所在目录。");
        var tmp = Path.Combine(dir, $".{Path.GetFileName(origPath)}.keenrestore.{Guid.NewGuid():N}.tmp");

        using (var src = new FileStream(verPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                     FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
                     FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            await src.CopyToAsync(dst, ct);
            await dst.FlushAsync(ct);
            dst.Flush(flushToDisk: true);
        }

        try { await MoveWithRetryAsync(tmp, origPath, ct); }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        // ③ 刷新管线基线(快照刚落库)
        await _pipeline.ReSeedBaselineAsync(target.WatchedGuid);
    }

    private static async Task MoveWithRetryAsync(string tmp, string dest, CancellationToken ct)
    {
        const int max = 6;
        for (int i = 0; i < max; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { File.Move(tmp, dest, overwrite: true); return; }
            catch (IOException) when (i < max - 1) { await Task.Delay(500 << i, ct); }
            catch (UnauthorizedAccessException) when (i < max - 1) { await Task.Delay(500 << i, ct); }
        }
        throw new IOException("覆盖原文件失败(可能被编辑器独占)。请关闭编辑器后重试。");
    }
}
