using Keen.Models;
using Microsoft.Extensions.Logging;

namespace Keen.Services;

// 可逆恢复(不变量⑧)。
// ① 先把「当前原文件」捕进库(kind=PreRestoreSnapshot,bypass 去重)——保证恢复前的字节永远在历史里。
//    已停止监控的文件不在管线里 → 临时注册再捕获,完事后移除(否则「历史仍可用」的承诺对恢复是假的)。
// ② 用 File.Copy(版本 blob → 原文件, overwrite) 直接覆盖;不在原文件目录留任何临时文件(不污染用户目录)。
//    原子性由前置快照 + 版本 blob 永久在库兜底——覆盖中途崩溃,旧状态在「恢复前快照」、目标在历史 blob,重试即恢复。
// ③ 覆盖会触发 watcher → 管线自动把恢复后的内容作为 Normal 版本入库(恢复结果留痕)。
internal sealed class RestoreService
{
    private readonly VaultStore _store;
    private readonly VaultIndex _index;
    private readonly BackupPipeline _pipeline;

    public RestoreService(VaultStore store, VaultIndex index, BackupPipeline pipeline)
    {
        _store = store; _index = index; _pipeline = pipeline;
    }

    public async Task RestoreAsync(VersionEntry target, CancellationToken ct = default)
    {
        var origPath = target.OrigPathAtCapture;
        if (!File.Exists(origPath))
            throw new FileNotFoundException("原文件当前不存在(可能被删除或移动):", origPath);
        var verPath = _store.FullPath(target.StoredRelPath);
        if (!File.Exists(verPath))
            throw new FileNotFoundException("历史版本文件缺失:", target.StoredRelPath);

        // 已停止监控的文件:临时注册进管线,否则前置快照必失败。
        var wasRegistered = _pipeline.IsRegistered(target.WatchedGuid);
        if (!wasRegistered)
            await _pipeline.RegisterAsync(target.WatchedGuid, origPath, target.OrigFilename);
        try
        {
            // ① 前置快照(bypass 去重,同步等待落地)
            var snap = await _pipeline.CaptureDirectAsync(target.WatchedGuid, origPath, target.OrigFilename,
                VersionKind.PreRestoreSnapshot, bypassDedupe: true, ct);
            if (snap is null)
                throw new InvalidOperationException("无法为恢复创建前置快照(捕获失败,请查看日志)。");

            // ② 直接覆盖原文件(不在用户目录留临时)。
            await CopyWithRetryAsync(verPath, origPath, ct);

            // ③ 刷新管线基线(快照刚落库)
            await _pipeline.ReSeedBaselineAsync(target.WatchedGuid);
        }
        finally
        {
            if (!wasRegistered)
                await _pipeline.StopFileAsync(target.WatchedGuid, TimeSpan.FromSeconds(2));
        }
    }

    private static async Task CopyWithRetryAsync(string src, string dest, CancellationToken ct)
    {
        const int max = 6;
        for (int i = 0; i < max; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { File.Copy(src, dest, overwrite: true); return; }
            // 不加 when(i<max-1):最后一次失败要落到循环外的友好错误,而不是裸异常漏给用户
            catch (IOException) when (i < max - 1) { await Task.Delay(500 << i, ct); }
            catch (UnauthorizedAccessException) when (i < max - 1) { await Task.Delay(500 << i, ct); }
        }
        throw new IOException("覆盖原文件失败(可能被编辑器独占)。请关闭编辑器后重试。");
    }
}
