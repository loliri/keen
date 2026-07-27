using Keen.Forms;
using Keen.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Keen;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppPaths.Ensure();
        ApplicationConfiguration.Initialize();

        using var single = new SingleInstance();
        if (!single.TryAcquire())
        {
            // 已有实例在跑;静默退出。
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(AppPaths.LogDir, "keen-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            Log.Information("Keen 启动");

            var config = new ConfigService();
            config.LoadAsync().GetAwaiter().GetResult();
            Directory.CreateDirectory(config.Current.VaultRoot);

            var store = new VaultStore(config.Current.VaultRoot);
            var index = new VaultIndex(Path.Combine(config.Current.VaultRoot, "keen.sqlite"));

            using var services = BuildServices(config, store, index);
            using var ctx = new TrayAppContext(services);
            ctx.StartupAsync().GetAwaiter().GetResult();
            Application.Run(ctx);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Keen 未处理异常");
            throw;
        }
        finally
        {
            Log.Information("Keen 退出");
            Log.CloseAndFlush();
        }
    }

    private static ServiceProvider BuildServices(ConfigService config, VaultStore store, VaultIndex index)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: false));
        services.AddSingleton(config);
        services.AddSingleton(store);
        services.AddSingleton(index);
        services.AddSingleton<BackupPipeline>(sp => new BackupPipeline(
            sp.GetRequiredService<VaultStore>(),
            sp.GetRequiredService<VaultIndex>(),
            sp.GetRequiredService<ILogger<BackupPipeline>>(),
            config.Current.SkipIdentical));
        services.AddSingleton<FileWatchService>();
        services.AddSingleton<MainForm>();
        return services.BuildServiceProvider();
    }
}
