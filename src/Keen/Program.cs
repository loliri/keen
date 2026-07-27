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
            // 已有实例在跑;M2 起会经命名管道把它带到前台,这里先静默退出。
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
            using var services = BuildServices();
            using var ctx = new TrayAppContext(services);
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

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(dispose: false));
        services.AddSingleton<ConfigService>();
        return services.BuildServiceProvider();
    }
}
