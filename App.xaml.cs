using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WSLCManager.Helpers;

namespace WSLCManager;

public partial class App : Application
{
    /// <summary>崩溃日志位置：%LOCALAPPDATA%\WSLCManager\error.log</summary>
    public static string CrashLog => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSLCManager", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings.Load();

        // 软件渲染：不创建 D3D 设备 → 显卡用户态驱动（igd12dxva64/igc64 等约 150MB+）
        // 的页不会被 fault in。必须在创建任何 Window 之前设置，否则无效。
        // 注意：软件渲染下 ClearType 会退化成灰度抗锯齿，中文小字会发虚，
        // 所以默认走硬件渲染；需要时用 --software 或设置项开启。
        bool forceSoftware = e.Args.Any(a => a.Equals("--software", StringComparison.OrdinalIgnoreCase));
        if (AppSettings.SoftwareRendering || forceSoftware)
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // 文字排版模式按 DPI 自适应（125% 以上用 Ideal，否则用 Display）。
        // 详见 Helpers/TextQuality.cs 顶部注释 —— 这是"字体发虚/边缘锯齿"的修复点。
        var textModeArg = e.Args
            .FirstOrDefault(a => a.StartsWith("--textmode=", StringComparison.OrdinalIgnoreCase));
        if (textModeArg != null)
            TextQuality.Override = textModeArg.Substring("--textmode=".Length).Trim();
        TextQuality.Install();

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            WriteCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            WriteCrash("Task", args.Exception);
            args.SetObserved();
        };

        WriteCrashMark($"启动 {GetType().Assembly.GetName().Version}" +
                       $" | 渲染={(AppSettings.SoftwareRendering ? "Software" : "Hardware")}" +
                       $" | 最小化回收={(AppSettings.TrimOnMinimize ? "on" : "off")}" +
                       $" | {TextQuality.Describe()}" +
                       $" | 配置={AppSettings.Location}");
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash("Dispatcher", e.Exception);
        try
        {
            Dialogs.AppDialog.Info(MainWindow, "出错了",
                e.Exception.Message + "\n\n详细信息已写入日志：" + CrashLog);
        }
        catch { }
        e.Handled = true;
    }

    private static void WriteCrashMark(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog));
            File.AppendAllText(CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] --- {text} ---\n");
        }
        catch { }
    }

    private static void WriteCrash(string src, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog));
            File.AppendAllText(CrashLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] !! {src}\n{ex}\n\n");
        }
        catch { }
    }
}
