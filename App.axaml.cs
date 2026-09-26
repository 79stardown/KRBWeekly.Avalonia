using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KRBWeekly.Helpers;
using KRBWeekly.Services;
using KRBWeekly.Views;

namespace KRBWeekly;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 日志初始化：exe 旁 Data 目录（与旧版路径约定一致）
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppContext.BaseDirectory;
        LogService.Init(Path.Combine(exeDir, "Data"));

        // 全局异常兜底：写 crash.log + 运行日志
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleFatal(e.ExceptionObject as Exception, "未处理异常");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            HandleFatal(e.Exception, "未观察任务异常");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 外观先于窗口创建应用（窗口还没构造，切主题不会闪一下）
            var cfg = new ConfigService(Path.Combine(exeDir, "Data")).Load();
            ThemeService.ApplyThemeMode(cfg.ThemeMode);
            ThemeService.ApplyAccent(cfg.AccentColor);

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void HandleFatal(Exception? ex, string source)
    {
        try
        {
            var msg = $"[!] {source}: {ex}";
            LogService.Append(msg);
            var logDir = LogService.LogDir;
            if (!string.IsNullOrEmpty(logDir))
                File.WriteAllText(Path.Combine(logDir, "crash.log"), msg);
        }
        catch
        {
            // 兜底本身失败不抛
        }
    }
}
