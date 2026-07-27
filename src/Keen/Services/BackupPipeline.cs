using System.Collections.Concurrent;
using System.Threading.Channels;
using Keen.Models;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace Keen.Services;

// 5 段备份管线(不变量①~⑦)。每个被监控文件一个有界 Channel + 单消费者;全局 SemaphoreSlim(4) 节流。
// ① 500ms 尾沿防抖 → ② 写静止/锁重试(20 次 ~30s)+ FileId 校验打开 → ④ 流复制+SHA256 → ⑤ 持久落库+插索引。
// 消费循环内 try/catch,单个坏作业不杀消费线程。
internal sealed class BackupPipeline : IDisposable
{
    private readonly VaultStore _store;
    private readonly VaultIndex _index;
    private readonly ILogger<BackupPipeline> _log;
    private readonly bool _skipIdentical;

    private readonly SemaphoreSlim _globalThrottle = new(4);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, FileState> _states = new();
    private readonly ConcurrentDictionary<Guid, Timer> _debounce = new();
    private readonly Random _jitter = new(); // 重试抖动;仅在消费线程内用

    public event Action<VersionEntry>? VersionCaptured;
    public event Action<Guid, FileHealth, string?>? HealthChanged;

    public BackupPipeline(VaultStore store, VaultIndex index, ILogger<BackupPipeline> log, bool skipIdentical)
    {
        _store = store; _index = index; _log = log; _skipIdentical = skipIdentical;
    }

    private sealed class FileState
    {
        public Guid Guid;
        public string Path = "";
        public string DisplayName = "";
        public Channel<BackupJob> Channel = null!;
        public Task Consumer = Task.CompletedTask;
        public int Seq;
        public long LastTicks;
        public string? LastSha;
        public long LastSize;
    }

    public async Task RegisterAsync(Guid guid, string path, string displayName)
    {
        if (_states.ContainsKey(guid)) return;
        var st = new FileState { Guid = guid, Path = path, DisplayName = displayName };
        st.Channel = Channel.CreateBounded<BackupJob>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var last = await _index.GetLastVersionAsync(guid);
        if (last is { } lv)
        {
            st.LastSha = lv.Sha256; st.LastSize = lv.SizeBytes; st.LastTicks = lv.CapturedAtTicks; st.Seq = lv.Seq;
        }
        if (_states.TryAdd(guid, st))
            st.Consumer = Task.Run(() => ConsumeAsync(st));
    }

    public void Unregister(Guid guid) => EnqueueAndComplete(guid);

    private void EnqueueAndComplete(Guid guid)
    {
        if (_states.TryGetValue(guid, out var st))
            st.Channel.Writer.TryComplete();
    }

    // FSW 事件入口:500ms 尾沿防抖,最后一次胜出。
    public void OnFileChanged(Guid guid)
    {
        if (!_states.TryGetValue(guid, out var st)) return;
        if (_debounce.TryGetValue(guid, out var prev)) prev.Dispose();
        var path = st.Path;
        var name = st.DisplayName;
        var t = new Timer(_ =>
            Enqueue(new BackupJob { WatchedGuid = guid, SourcePath = path, DisplayName = name }),
            null, 500, Timeout.Infinite);
        _debounce[guid] = t;
    }

    public void EnqueueCatchup(Guid guid, string path, string displayName)
        => Enqueue(new BackupJob { WatchedGuid = guid, SourcePath = path, DisplayName = displayName });

    public void Enqueue(BackupJob job)
    {
        if (_states.TryGetValue(job.WatchedGuid, out var st))
            st.Channel.Writer.TryWrite(job);
    }

    // watcher / 其它路径用它来直接设健康状态(UI 单一来源由此转发)。
    public void MarkHealth(Guid guid, FileHealth h, string? msg) => HealthChanged?.Invoke(guid, h, msg);

    private async Task ConsumeAsync(FileState st)
    {
        var token = _cts.Token;
        await foreach (var job in st.Channel.Reader.ReadAllAsync(token))
        {
            try { await ProcessJobAsync(st, job); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "处理 {Guid} 的备份作业失败", st.Guid);
                HealthChanged?.Invoke(st.Guid, FileHealth.Degraded, ex.Message);
            }
        }
    }

    private async Task ProcessJobAsync(FileState st, BackupJob job)
    {
        await _globalThrottle.WaitAsync(_cts.Token);
        HealthChanged?.Invoke(st.Guid, FileHealth.Syncing, null);
        try
        {
            const int maxAttempts = 20;
            for (int attempt = 0; attempt < maxAttempts && !_cts.IsCancellationRequested; attempt++)
            {
                try
                {
                    var info = new FileInfo(job.SourcePath);
                    if (!info.Exists) { HealthChanged?.Invoke(st.Guid, FileHealth.Missing, "源文件不存在"); return; }

                    // 大文件(>100MB)写静止:2 样本长度稳定,≥1s。
                    if (info.Length > 100L << 20 && attempt == 0)
                    {
                        long s1 = info.Length;
                        await Task.Delay(1000, _cts.Token);
                        var info2 = new FileInfo(job.SourcePath);
                        if (!info2.Exists) { HealthChanged?.Invoke(st.Guid, FileHealth.Missing, null); return; }
                        if (info2.Length != s1) { await Task.Delay(DelayFor(attempt), _cts.Token); continue; }
                    }

                    using var src = SourceFile.OpenVerified(job.SourcePath);

                    int seq = st.Seq + 1; // 暂定;成功才提交
                    long ticks = Math.Max(st.LastTicks, DateTime.UtcNow.Ticks);
                    if (ticks <= st.LastTicks) ticks = st.LastTicks + 1;
                    var month = new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM");
                    var ext = Path.GetExtension(job.DisplayName);
                    var rel = $"{st.Guid}/{month}/{ticks:D19}_{seq:D4}{ext}";

                    var copy = await _store.WriteBlobAsync(src, rel, progress: null, _cts.Token);

                    // 去重(默认关;PreRestoreSnapshot 等永远 bypass)
                    if (!job.BypassDedupe && _skipIdentical && st.LastSha == copy.sha256)
                    {
                        _store.DeleteBlob(rel);
                        st.LastTicks = ticks;
                        HealthChanged?.Invoke(st.Guid, FileHealth.Watching, null);
                        return;
                    }

                    var entry = await _index.InsertVersionAsync(st.Guid, ticks, seq, job.Kind, rel,
                        job.SourcePath, job.DisplayName, copy.size, copy.sha256);
                    st.Seq = seq; st.LastSha = copy.sha256; st.LastSize = copy.size; st.LastTicks = ticks;
                    VersionCaptured?.Invoke(entry);
                    HealthChanged?.Invoke(st.Guid, FileHealth.Watching, null);
                    return;
                }
                catch (FileNotFoundException) { HealthChanged?.Invoke(st.Guid, FileHealth.Missing, null); return; }
                catch (OperationCanceledException) { return; }
                catch (StaleHandleException) when (attempt < maxAttempts - 1) { await Task.Delay(DelayFor(attempt), _cts.Token); }
                catch (IOException ex) when (attempt < maxAttempts - 1)
                {
                    _log.LogWarning("捕获重试 {Guid} attempt {A}: {Msg}", st.Guid, attempt, ex.Message);
                    await Task.Delay(DelayFor(attempt), _cts.Token);
                }
            }
            HealthChanged?.Invoke(st.Guid, FileHealth.Failing, "重试耗尽");
        }
        finally
        {
            _globalThrottle.Release();
        }
    }

    private int DelayFor(int attempt)
    {
        int b = Math.Min(100 << attempt, 2000);
        lock (_jitter) return b + (int)(_jitter.NextDouble() * b * 0.2);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        foreach (var st in _states.Values) st.Channel.Writer.TryComplete();
        foreach (var t in _debounce.Values) t.Dispose();
        _debounce.Clear();
        try { await Task.WhenAll(_states.Values.Select(s => s.Consumer)); }
        catch { }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _globalThrottle.Dispose();
    }
}
