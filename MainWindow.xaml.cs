using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WSLCManager.Dialogs;
using WSLCManager.Helpers;
using WSLCManager.Models;
using WSLCManager.Services;

namespace WSLCManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ContainerInfo> _containers = new();
    private readonly ObservableCollection<ImageInfo> _images = new();
    private readonly ObservableCollection<ImageSearchItem> _searchResults = new();
    private readonly DispatcherTimer _realtime = new();

    private WslEnvStatus _env;
    private bool _ready;
    private bool _busy;
    private bool _reallyExit;
    private CancellationTokenSource _statsCts;
    private TrayIcon _tray;

    private ContainerInfo _selContainer;
    private ImageInfo _selImage;

    /// <summary>
    /// 「常用镜像源」预设（设置页的可点按钮）。
    ///
    /// 默认值是**第三方公开**加速地址（见 `AppSettings.DefaultMirrorPresets`）——
    /// 本项目只是替用户填个地址，不提供、不运营、也不背书这些站点，可用性/速度不保证。
    /// 用户可在 `settings.ini` 里改 `MirrorPresets=`（写 `none` 表示一个都不用）。
    /// </summary>
    private static string[] MirrorPresets() =>
        (AppSettings.MirrorPresets ?? "")
            .Split(new[] { ',', '，', ';', '；', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public MainWindow()
    {
        InitializeComponent();
        ChromeHelper.Attach(this, 52, new FrameworkElement[] { BtnMin, BtnMax, BtnClose });

        GridContainers.ItemsSource = _containers;
        GridImages.ItemsSource = _images;
        ListSearchResults.ItemsSource = _searchResults;

        _realtime.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(AppSettings.StatsIntervalMs, 1000, 60000));
        _realtime.Tick += async (s, e) => await RefreshRealtimeStatsAsync();

        ListMirrors.ItemsSource = MirrorPresets();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (s, e) => ApplyMaxIcon();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        LoadSettingsIntoUi();
        ApplyMaxIcon();

        try
        {
            ImgLogo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
        }
        catch { }

        // 系统托盘（关闭窗口时最小化到后台）
        try
        {
            _tray = new TrayIcon("WSLC 容器管理器");
            _tray.ShowRequested += RestoreFromTray;
            _tray.ExitRequested += ExitApp;
        }
        catch { }

        TxtTitleHint.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.9.0");

        TxtAbout.Text =
            "WSLC 容器管理器 —— WSL 容器（wslc）的可视化工具，仅调用官方 wsl / wslc 命令。\r\n" +
            "依赖 WSL 2.9.3 及以上版本（wslc 随 WSL 提供）。\r\n" +
            "设置里预置的镜像加速地址为第三方公开站点，与本工具作者无关，可用性不保证。";

        await DetectAsync();
        await RefreshContainersAsync(silent: true);
        await RefreshImagesAsync(silent: true);

        ApplyStartupPage();
        ApplyStartupSearch();
        _ = Dispatcher.BeginInvoke(new Action(ApplyStartupDialog), DispatcherPriority.Background);

        ApplyStartupDiagnostics();

        // --click-start=<容器名>：走真实 UI 路径点一次「启动」，导出界面文案（见方法注释）
        await ClickStartAsync();

        await DumpLayoutAsync();
        await HandleScreenshotAsync();

        // 兜底：只带 --dump-layout / --textmode 而不带 --screenshot 时，
        // HandleScreenshotAsync 会直接 return，没人退进程 → 命令行永远挂着（实测踩过）。
        ExitIfRequested();
    }

    // ────────────────────────── 验收用启动参数 ──────────────────────────
    // 只在显式带参数时生效，正常运行完全不受影响。
    // 存在的理由：「按钮有没有被裁掉」「列表有没有滚动条」这类结论靠截图定不了案
    // ——截图是像素，差几个 px 肉眼分不出来，必须打印真实布局矩形来量。

    private void ApplyStartupDiagnostics()
    {
        var argv = Environment.GetCommandLineArgs();

        // --size=WxH：指定窗口尺寸。WPF 里 Width 小于 MinWidth 时 MinWidth 生效，
        // 所以传一个小值就等于"按最小宽度跑"，正好用来验证窄窗口下的表现。
        var sizeArg = argv.FirstOrDefault(a => a.StartsWith("--size=", StringComparison.OrdinalIgnoreCase));
        if (sizeArg != null)
        {
            var p = sizeArg.Substring("--size=".Length).Split('x', 'X');
            if (p.Length == 2
                && double.TryParse(p[0], System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var w)
                && double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var h))
            {
                Width = w;
                Height = h;
            }
        }

        ApplyStressData();
        ApplyDemoMode();
        ApplyFillDemo();
    }

    /// <summary>
    /// --demo：**纯演示数据**模式，用于对外截图 / 文档配图。
    /// 清掉本机真实的容器、镜像与状态栏版本信息，只留构造出来的样例，
    /// 避免把本机环境（容器名、版本号等）带进公开材料。
    /// </summary>
    private void ApplyDemoMode()
    {
        if (!Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
            return;

        _containers.Clear();
        _images.Clear();

        // 关键：停掉实时刷新定时器，否则它会在截图前把演示数据换成真实数据
        try { _realtime.Stop(); } catch { }

        var demo = new (string Name, string Image, string Ports, bool Running, double Cpu, double Mem)[]
        {
            ("web-frontend",  "library/nginx:1.27-alpine",  "8080:80",   true,  0.42, 3.10),
            ("api-server",    "library/python:3.12-slim",   "5000:5000", true,  1.85, 12.40),
            ("redis-cache",   "library/redis:7-alpine",     "6379:6379", true,  0.15, 1.20),
            ("postgres-main", "library/postgres:16",        "5432:5432", true,  0.90, 8.65),
            ("worker-queue",  "library/python:3.12-slim",   "",          false, 0.00, 0.00),
            ("nginx-gateway", "library/nginx:stable",       "80:80,443:443", false, 0.00, 0.00),
        };

        for (int i = 0; i < demo.Length; i++)
        {
            var d = demo[i];
            var c = new ContainerInfo
            {
                Id = $"demo{i:D12}",
                Name = d.Name,
                Image = d.Image,
                Ports = d.Ports,
                Status = d.Running ? "Up 12 minutes" : "Exited (0) 3 minutes ago",
            };
            c.ApplyStatus(d.Running ? "running" : "exited", c.Status);
            if (d.Running)
                c.ApplyStats(new ContainerStats
                {
                    Cpu = $"{d.Cpu:F2}%",
                    MemPercent = $"{d.Mem:F2}%",           // 列表内存列的进度条读这个
                    Mem = $"{d.Mem * 25:F0}MB / 2GB",
                    Net = "1.2MB / 0.8MB",
                    Pids = "12"
                });
            _containers.Add(c);
        }

        // 状态栏不带本机版本号
        Ui(() => TxtStatus.Text = "就绪");
    }

    /// <summary>--fill-demo：往镜像页两个输入框填演示文本，便于截图核对排版（对齐 / 内边距）。</summary>
    private void ApplyFillDemo()
    {
        if (!Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--fill-demo", StringComparison.OrdinalIgnoreCase)))
            return;

        TxtPullImage.Text = "docker.1ms.run/library/nginx:latest";
        TxtRepoQuery.Text = "nginx-stable-alpine-very-long-keyword";
    }

    /// <summary>
    /// --stress=N：往两个列表灌 N 行假数据（纯内存，不碰 wslc、不写盘），把列表撑出滚动条。
    /// 做成幂等：刷新定时器会把列表清空重填，量测前需要能再灌一次而不叠加。
    /// </summary>
    private void ApplyStressData()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--stress=", StringComparison.OrdinalIgnoreCase));
        if (arg == null || !int.TryParse(arg.Substring("--stress=".Length), out var n) || n <= 0) return;
        n = Math.Min(n, 80);

        for (int i = _containers.Count - 1; i >= 0; i--)
            if ((_containers[i].Name ?? "").StartsWith("demo-container-", StringComparison.Ordinal))
                _containers.RemoveAt(i);
        for (int i = _images.Count - 1; i >= 0; i--)
            if ((_images[i].Repository ?? "").Contains("/library/demo", StringComparison.Ordinal))
                _images.RemoveAt(i);

        for (int i = 1; i <= n; i++)
        {
            _containers.Add(new ContainerInfo
            {
                Id = string.Format("stress{0:D12}", i),
                Name = $"demo-container-{i:00}",
                Image = "registry.example.com/library/alpine:latest",
                State = i % 3 == 0 ? ContainerState.Exited : ContainerState.Running,
                Status = i % 3 == 0 ? "Exited (0)" : "Up 2 minutes"
            });
            _images.Add(new ImageInfo
            {
                Repository = "registry.example.com/library/demo" + i,
                Tag = "latest",
                Id = string.Format("stress{0:D12}abcdef", i),
                Size = "12.3MB",
                Created = "2026-10-10 00:00:00 +0800 CST"
            });
        }
    }

    /// <summary>
    /// --dump-layout=&lt;路径&gt;：把关键控件的真实布局矩形写成文本。
    /// 必须等布局稳定后再量：DataGrid 的列宽与 ScrollViewer 视口是渲染阶段才算出来的，
    /// 紧接着 UpdateLayout() 去读会拿到中间值（列宽全 20、ViewportHeight=13 这种），
    /// 拿中间值下结论等于没量。
    /// </summary>
    private async System.Threading.Tasks.Task DumpLayoutAsync()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--dump-layout=", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;

        var path = arg.Substring("--dump-layout=".Length).Trim().Trim('"');
        if (path.Length == 0) return;

        await System.Threading.Tasks.Task.Delay(1500);
        ApplyStressData();   // 刷新定时器可能已把列表清空重填，量测前补灌一次
        UpdateLayout();
        try { File.WriteAllText(path, BuildLayoutReport(), new UTF8Encoding(false)); } catch { }
    }

    private string BuildLayoutReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 窗口 ==");
        sb.AppendLine($"  尺寸 {ActualWidth:0.#} x {ActualHeight:0.#}   MinWidth={MinWidth} MinHeight={MinHeight}");
        sb.AppendLine();
        sb.AppendLine("== 文字渲染 ==");
        sb.AppendLine("  " + TextQuality.Describe(this));
        var realMode = TextOptions.GetTextFormattingMode(this);
        var realAa = TextOptions.GetTextRenderingMode(this);
        sb.AppendLine($"  实际生效 TextFormattingMode={realMode}  TextRenderingMode={realAa}");
        sb.AppendLine($"  窗口是否分层（AllowsTransparency）={AllowsTransparency}" +
                      "   （分层窗口 WPF 一律关 ClearType，弹窗靠 RenderOptions.ClearTypeHint 找回来）");
        // 抽查一个弹窗式分层窗口卡片上有没有开 ClearTypeHint
        sb.AppendLine();
        AppendGridReport(sb, GridContainers, "容器列表");
        AppendGridReport(sb, GridImages, "镜像列表");
        return sb.ToString();
    }

    private void AppendGridReport(StringBuilder sb, DataGrid dg, string label)
    {
        var origin = dg.TransformToAncestor(this).Transform(new Point(0, 0));
        var sv = FindDescendant<ScrollViewer>(dg);

        sb.AppendLine($"== {label} ==");
        sb.AppendLine($"  位置 x={origin.X:0.#} y={origin.Y:0.#}   尺寸 {dg.ActualWidth:0.#} x {dg.ActualHeight:0.#}");
        sb.AppendLine($"  行数 {dg.Items.Count}");
        if (sv != null)
        {
            sb.AppendLine($"  垂直滚动条 {sv.ComputedVerticalScrollBarVisibility}   水平滚动条 {sv.ComputedHorizontalScrollBarVisibility}");
            sb.AppendLine($"  视口 {sv.ViewportWidth:0.#} x {sv.ViewportHeight:0.#}   可滚动 {sv.ScrollableWidth:0.#} x {sv.ScrollableHeight:0.#}");
        }
        var cols = new List<string>();
        foreach (var c in dg.Columns) cols.Add($"{c.Header}={c.ActualWidth:0.#}");
        sb.AppendLine("  列宽 " + string.Join("  ", cols));

        // 行内按钮：必须按「行」分组量 —— 运行中的行比已停止的行多一个按钮（强制），
        // 只抽第一行会漏掉这一行，正是它才可能被裁。
        // 关键：判定边界要用「内容视口右边缘」，而不是 DataGrid 自身右边缘 ——
        // 垂直滚动条约占 17.6px，用 DataGrid 宽度当边界会得出假绿的「在界内」。
        var svOrigin = sv != null ? sv.TransformToAncestor(this).Transform(new Point(0, 0)) : origin;
        double contentLeft = svOrigin.X;
        double contentRight = sv != null ? svOrigin.X + sv.ViewportWidth : origin.X + dg.ActualWidth;

        sb.AppendLine($"  内容视口 x={contentLeft:0.#}..{contentRight:0.#}（列宽合计 {dg.Columns.Sum(c => c.ActualWidth):0.#}）");

        var perRow = new Dictionary<int, List<(string name, double l, double r, double w)>>();
        int idx = 0;
        foreach (var b in Descendants<Button>(dg))
        {
            var row = FindAncestor<DataGridRow>(b);
            if (row == null) continue;
            int ri = row.GetIndex();
            var p = b.TransformToAncestor(this).Transform(new Point(0, 0));
            if (!perRow.TryGetValue(ri, out var list)) perRow[ri] = list = new List<(string, double, double, double)>();
            list.Add((b.Content?.ToString() ?? "", p.X, p.X + b.ActualWidth, b.ActualWidth));
            idx++;
        }

        sb.AppendLine($"  行内按钮 共 {idx} 个，覆盖 {perRow.Count} 行");
        foreach (var kv in perRow.OrderBy(k => k.Key).Take(6))
        {
            var visible = kv.Value.Where(b => b.w > 0.5).ToList();
            if (visible.Count == 0) continue;
            double maxRight = visible.Max(b => b.r);
            double need = maxRight - contentLeft;
            sb.AppendLine($"    行#{kv.Key} {visible.Count} 个可见按钮  {visible[0].l:0.#}..{maxRight:0.#}  需 {need:0.#}px"
                        + $"  {(maxRight > contentRight + 0.5 ? $"越界 {(maxRight - contentRight):0.#}px!" : "在界内")}");
            sb.AppendLine($"       " + string.Join(" ", visible.Select(b => $"[{b.name}]")));
        }
        sb.AppendLine();
    }

    private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T t) return t;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindDescendant<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    /// <summary>
    /// --screenshot=&lt;路径&gt;：把窗口用 WPF 渲染管线导出为 PNG（不依赖 DWM/窗口是否可见），
    /// 供自动化验收使用；配合 --exit-after-shot 截完即退出。
    /// </summary>
    private async System.Threading.Tasks.Task HandleScreenshotAsync()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;

        var path = arg.Substring("--screenshot=".Length).Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) return;

        await System.Threading.Tasks.Task.Delay(2500);

        string err = null;
        try
        {
            // --grab：连 DWM 绘制的非客户区一起抓（用于验证窗口外框）。
            // 普通模式用 RenderTargetBitmap，只覆盖 WPF 自己的可视化树。
            bool grab = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--grab", StringComparison.OrdinalIgnoreCase));

            if (grab)
            {
                await GrabFromScreenAsync(path);
            }
            else
            {
                // --dialog 模式下渲染的是被打开的对话框，而不是主窗口
                var target = _shotDialog != null ? (FrameworkElement)_shotDialog : this;
                var dpi = VisualTreeHelper.GetDpi(target);
                var rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                rtb.Render(target);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var fs = File.Create(path);
                enc.Save(fs);
            }
        }
        catch (Exception ex) { err = ex.ToString(); }

        if (err != null)
        {
            try { File.WriteAllText(path + ".err.txt", err); } catch { }
        }

        ExitIfRequested();
    }

    /// <summary>
    /// --click-start=&lt;容器名&gt; [--dump-ui=&lt;路径&gt;]：在**真实界面上按下**该行的「启动」按钮，
    /// 然后把状态栏、操作日志、列表状态列的**真实文本**导出。
    ///
    /// 为什么必须真的去点：本次修的正是「提示成功、状态还是已停止」——
    /// 对错取决于三处文本的组合（状态栏 / 操作日志 / 列表状态），
    /// 只调服务层（`WslcService.StartAsync` + `InspectStateAsync`）证明不了**界面文案**对不对。
    /// 默认输出到程序目录下的 click_start_result.txt。
    /// </summary>
    private async System.Threading.Tasks.Task ClickStartAsync()
    {
        var argv = Environment.GetCommandLineArgs();
        var arg = argv.FirstOrDefault(a => a.StartsWith("--click-start=", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;

        var name = arg.Substring("--click-start=".Length).Trim().Trim('"');
        var outArg = argv.FirstOrDefault(a => a.StartsWith("--dump-ui=", StringComparison.OrdinalIgnoreCase));
        var outPath = outArg?.Substring("--dump-ui=".Length).Trim().Trim('"') ?? "";
        if (outPath.Length == 0) outPath = Path.Combine(AppContext.BaseDirectory, "click_start_result.txt");

        var lines = new List<string> { "== 真实 UI 路径：点击「启动」 ==", "目标容器：" + name, "" };
        try
        {
            // 必须先切到容器页：DataGrid 是虚拟化的，而页面切走时容器页整块是 Collapsed
            // —— 折叠状态下不会生成行容器，视觉树里一个 DataGridRow 都找不到（踩过）。
            NavContainer.IsChecked = true;

            await System.Threading.Tasks.Task.Delay(2500);   // 与截图同一节奏：等列表落地
            UpdateLayout();
            GridContainers.UpdateLayout();

            var row = Descendants<DataGridRow>(GridContainers)
                .FirstOrDefault(r => (r.Item as ContainerInfo)?.Name == name);

            if (row == null)
            {
                lines.Add("!! 列表里找不到该容器行（可能没刷新出来）");
            }
            else
            {
                lines.Add("点前列表状态 : " + (row.Item as ContainerInfo)?.StateText);
                var btn = Descendants<Button>(row).FirstOrDefault(b => (b.Content as string) == "启动");

                if (btn == null)
                {
                    lines.Add("!! 该行没有「启动」按钮（运行中的容器只显示 停止/强制）");
                }
                else
                {
                    lines.Add($"按钮         : Content=\"启动\" IsEnabled={btn.IsEnabled}");
                    // 真的走一遍 Click —— 与用户手点完全同一条路径（OnRowStartClick → RowActionAsync）
                    btn.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent, btn));

                    await System.Threading.Tasks.Task.Delay(4500);   // 操作 + 复查(~1.1s) + 刷新

                    // ⚠ 刷新后**不能再用刚才那个 DataGridRow**：列表重建过，行容器已被回收，
                    //   row.Item 会变成 null（第一次量就踩了这个坑）。改成按名字去数据源里找。
                    var after = _containers.FirstOrDefault(x => x.Name == name);

                    lines.Add("");
                    lines.Add("状态栏文本   : " + TxtStatus.Text);
                    // ⚠ 状态色在**状态圆点**上（SetStatus 只改 ElpStatus.Fill），
                    //   文字色是固定的次要色，量它永远是灰的 —— 别量错对象。
                    lines.Add("状态栏圆点色 : " + (ElpStatus.Fill as SolidColorBrush)?.Color
                              + "   (OkBrush=#FF12A150  WarnBrush=#FFD9820A  ErrBrush=#FFE0484D)");
                    lines.Add("");
                    lines.Add("操作日志：");
                    foreach (var l in TableParser.Lines(TxtContainerLog.Text)) lines.Add("  " + l);
                    lines.Add("");
                    lines.Add("点后列表状态 : " + (after?.StateText ?? "(没找到)")
                              + "   （原始: " + (after?.Status ?? "") + "）");
                }
            }
        }
        catch (Exception ex) { lines.Add("!! 异常: " + ex); }

        try { File.WriteAllText(outPath, string.Join(Environment.NewLine, lines), new UTF8Encoding(false)); } catch { }
    }

    private bool _exitRequested;

    /// <summary>
    /// 带了 --exit-after-shot 就退出（幂等）。抽出来的原因：
    /// 只在截图分支里判会把「只导布局报告、不截图」的场景漏掉，那条路径没人退进程。
    /// </summary>
    private void ExitIfRequested()
    {
        if (_exitRequested) return;
        if (!Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--exit-after-shot", StringComparison.OrdinalIgnoreCase))) return;
        _exitRequested = true;
        ExitApp();
    }

    /// <summary>把窗口置顶后用屏幕抓取保存（含 DWM 非客户区），用于验证窗口外框。</summary>
    private async System.Threading.Tasks.Task GrabFromScreenAsync(string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0040);
        NativeMethods.SetForegroundWindow(hwnd);
        await System.Threading.Tasks.Task.Delay(700);

        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            // 关闭 = 最小化到后台（托盘）
            e.Cancel = true;
            HideToTray();
            return;
        }

        _realtime.Stop();
        try { _statsCts?.Cancel(); } catch { }
        try { _tray?.Dispose(); } catch { }
        MemoryTrimmer.Trim();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        try { _tray?.ShowBalloon("WSLC 容器管理器", "已最小化到托盘，双击图标可恢复。"); } catch { }
        SetStatus("已最小化到系统托盘", "InfoBrush");
    }

    private void RestoreFromTray()
    {
        Ui(() =>
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApp()
    {
        Ui(() =>
        {
            _reallyExit = true;
            try { _tray?.Dispose(); } catch { }
            Close();
            Application.Current?.Shutdown();
        });
    }

    private void ApplyStartupPage()
    {
        try
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;

            switch (arg.Substring("--page=".Length).Trim().ToLowerInvariant())
            {
                case "container": NavContainer.IsChecked = true; break;
                case "image": NavImage.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
                case "env": NavEnv.IsChecked = true; break;
            }
        }
        catch { }
    }

    /// <summary>
    /// --search=关键字：启动后自动填入检索框并触发一次检索。
    /// 仅用于自动化验收截图，正常使用不受影响。
    /// </summary>
    private void ApplyStartupSearch()
    {
        try
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--search=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;

            var q = arg.Substring("--search=".Length).Trim().Trim('"');
            if (q.Length == 0) return;

            TxtRepoQuery.Text = q;
            OnRepoSearchClick(this, null);
        }
        catch { }
    }

    private void ApplyStartupDialog()
    {
        try
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--dialog=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;

            var v = arg.Substring("--dialog=".Length).Trim().ToLowerInvariant();

            // 截图/布局量测模式下改用非模态 Show()：ShowDialog 会阻塞 UI 线程，
            // 让随后的截图任务永远排不上队（表现为「截出来没有对话框」）。
            bool shotMode = Environment.GetCommandLineArgs()
                .Any(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--dump-layout=", StringComparison.OrdinalIgnoreCase));

            Window dlg = null;
            if (v == "create")
                dlg = new CreateContainerDialog(_images) { Owner = this };
            else if (v == "detail" && _containers.Count > 0)
                dlg = new ContainerDetailDialog(_containers[0]) { Owner = this };
            else if (v == "logs" && _containers.Count > 0)
                dlg = new ContainerLogsDialog(_containers[0]) { Owner = this };
            else if (v == "terminal" && _containers.Count > 0)
            {
                // --pick-custom：验收用，强制切到「自定义命令」分支后再截图
                bool demo = Environment.GetCommandLineArgs()
                    .Any(a => a.Equals("--pick-custom", StringComparison.OrdinalIgnoreCase));
                dlg = new TerminalDialog(_containers[0], demo) { Owner = this };
            }
            else if (v == "confirm")
            {
                var ad = AppDialog.Build("删除容器",
                    "确定要删除容器「wslctest」吗？\n该操作不可撤销。",
                    "删除", danger: true);
                ad.Owner = this;
                dlg = ad;
            }
            else if (v == "info")
            {
                var ad = AppDialog.Build("容器完整名称", "wslctest\n\n（已复制到剪贴板）",
                    "知道了", danger: false, showCancel: false);
                ad.Owner = this;
                dlg = ad;
            }

            if (dlg == null) return;

            if (shotMode)
            {
                _shotDialog = dlg;
                dlg.Show();
            }
            else
            {
                dlg.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            SetStatus("打开对话框失败：" + ex.Message, "ErrBrush");
        }
    }

    /// <summary>截图模式下被显式打开的对话框（非模态），截图时作为渲染目标。</summary>
    private Window _shotDialog;

    // ══════════════════════════ 窗口 ══════════════════════════
    private void OnMinClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>最大化 / 还原 图标随窗口状态切换。</summary>
    private void ApplyMaxIcon()
    {
        try
        {
            BtnMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
        catch { }
    }

    // ══════════════════════════ 通用 ══════════════════════════
    private void Ui(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.Invoke(a);
    }

    private void Log(string text, TextBox target = null)
    {
        Ui(() =>
        {
            var box = target ?? TxtLog;
            box.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
            box.ScrollToEnd();
        });
    }

    /// <summary>
    /// 忙状态只更新右下角提示，**不锁鼠标指针** —— 启停/检索等操作期间界面仍可用。
    /// </summary>
    private void SetBusy(bool busy, string what = "")
        => Ui(() =>
        {
            _busy = busy;
            TxtBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            TxtBusy.Text = busy ? "● " + what : "";
        });

    private void SetStatus(string text, string brushKey)
        => Ui(() =>
        {
            TxtStatus.Text = text;
            if (Application.Current.TryFindResource(brushKey) is Brush b) ElpStatus.Fill = b;
        });

    private static string One(string s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Replace("\r\n", " ").Replace('\n', ' ').Trim();

    private static ContainerInfo CtxC(object sender)
        => (sender as FrameworkElement)?.DataContext as ContainerInfo;

    private static ImageInfo CtxI(object sender)
        => (sender as FrameworkElement)?.DataContext as ImageInfo;

    // ══════════════════════════ 导航 ══════════════════════════
    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        PanelEnv.Visibility = NavEnv.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelContainer.Visibility = NavContainer.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelImage.Visibility = NavImage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavContainer.IsChecked == true)
        {
            _realtime.Start();
            _ = RefreshContainersAsync();
        }
        else
        {
            _realtime.Stop();
            if (NavImage.IsChecked == true) _ = RefreshImagesAsync();
        }
    }

    // ══════════════════════════ 环境 ══════════════════════════
    private async void OnDetectClick(object sender, RoutedEventArgs e) => await DetectAsync();

    private async System.Threading.Tasks.Task DetectAsync()
    {
        SetBusy(true, "正在检测环境…");
        Log("检测 WSL / wslc 环境…");

        WslEnvStatus st;
        try { st = await InstallService.DetectAsync(); }
        catch (Exception ex)
        {
            Log("检测失败：" + ex.Message);
            SetBusy(false);
            return;
        }

        _env = st;
        ApplyEnvToUi(st);
        SetBusy(false);
    }

    private void ApplyEnvToUi(WslEnvStatus st)
    {
        Ui(() =>
        {
            TxtVerWsl.Text = string.IsNullOrEmpty(st.WslVersion) ? "—" : st.WslVersion;
            TxtVerKernel.Text = string.IsNullOrEmpty(st.KernelVersion) ? "—" : st.KernelVersion;
            TxtVerWslcPath.Text = st.WslcFound ? st.WslcPath : "未安装";
            TxtVerDistro.Text = string.IsNullOrEmpty(st.DefaultDistro) ? "—" : st.DefaultDistro;
            TxtEnvSummary.Text = st.Summary;

            // 小字只留「需要用户做什么」。正常状态下 WSL 版本已经出现在上面的大字（Summary）
            // 和右侧「版本与兼容性」卡里，再复述一遍就是噪音 —— 同一屏出现两遍同样的版本号，
            // 用户看不出哪句有用。正常时整块收起，不留空档。
            var hint = "";
            if (!st.WslFound)
                hint = "未检测到 WSL，请点击下方「安装 WSL 运行时」。";
            else if (!st.WslcFound && !st.SupportsWslc)
                hint = $"当前 WSL 版本低于 wslc 的最低要求（{WslEnvStatus.MinWslForWslc}），请先升级。";
            else if (!st.WslcFound)
                hint = "wslc.exe 未在预期路径中找到，可尝试重新检测或重启终端。";

            TxtEnvDetail.Text = hint;
            TxtEnvDetail.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            ElpEnv.Fill = (Brush)Application.Current.FindResource(
                st.Ready ? "OkBrush" : (st.SupportsWslc ? "WarnBrush" : "ErrBrush"));
        });

        SetStatus(One(st.Summary),
            st.Ready ? "OkBrush" : (st.WslFound ? "WarnBrush" : "ErrBrush"));

        Log($"检测完成：WSL={st.WslVersion} wslc={(st.WslcFound ? st.WslcPath : "未安装")}");
    }

    private async void OnWslInstallClick(object sender, RoutedEventArgs e)
        => await RunWslCommandAsync(new[] { "--install" }, "安装");

    private async void OnWslUpdateStableClick(object sender, RoutedEventArgs e)
        => await RunWslCommandAsync(InstallService.OfficialUpdateArgs(false), "更新");

    private async void OnWslUpdatePreClick(object sender, RoutedEventArgs e)
        => await RunWslCommandAsync(InstallService.OfficialUpdateArgs(true), "更新到预览版");

    private async System.Threading.Tasks.Task RunWslCommandAsync(string[] args, string verb)
    {
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        var wsl = ProcRunner.ResolveExecutable("wsl.exe");
        if (wsl == null) { Log("未找到 wsl.exe，无法执行。"); return; }

        Log($"执行：wsl {string.Join(" ", args)}（官方渠道，下载耗时取决于网络）");
        SetBusy(true, $"正在{verb} WSL…");

        // wsl --update 在下载安装包期间**不输出任何进度**（实测要下 350 MB 左右），
        // 只靠它的 stdout 会让人以为卡死；这里另开一条旁路，盯 %TEMP% 里的安装包报速度。
        using var watchCts = new CancellationTokenSource();
        var watchTask = WatchWslDownloadAsync(watchCts.Token);

        var r = await ProcRunner.RunAsync(wsl, args, 0, default, s => Log(s));

        watchCts.Cancel();
        try { await watchTask; } catch { }

        SetBusy(false);
        Log("退出码：" + r.ExitCode);
        foreach (var line in TableParser.Lines(r.All)) Log("  " + line);

        await DetectAsync();
        await RefreshContainersAsync();
        await RefreshImagesAsync();
    }

    /// <summary>
    /// 旁路监视 `wsl --update` 的下载进度。
    /// 原理：wsl 把安装包下到 %TEMP%\wsl.&lt;版本&gt;.x64.msi，但自身不打印进度；
    /// 于是每秒采样该文件大小，算实时速度并周期性写入日志。
    /// </summary>
    private async System.Threading.Tasks.Task WatchWslDownloadAsync(CancellationToken ct)
    {
        var started = DateTime.Now;
        FileInfo seen = null;
        long lastSize = 0;
        var lastAt = DateTime.Now;
        double speed = 0;                       // bytes/s
        var lastReport = DateTime.MinValue;
        bool announced = false;

        while (!ct.IsCancellationRequested)
        {
            try { await System.Threading.Tasks.Task.Delay(1000, ct); }
            catch (System.Threading.Tasks.TaskCanceledException) { break; }
            if (ct.IsCancellationRequested) break;

            var f = DownloadProbe.FindActivePackage(started.AddSeconds(-3));
            if (f == null) continue;
            try { f.Refresh(); } catch { continue; }

            if (seen == null || seen.FullName != f.FullName)
            {
                // 换了个文件（新的下载开始）
                seen = f;
                lastSize = 0;
                lastAt = DateTime.Now;
                announced = false;
            }

            var now = DateTime.Now;
            var dt = (now - lastAt).TotalSeconds;
            if (dt >= 0.9)
            {
                var delta = f.Length - lastSize;
                if (delta > 0) speed = delta / dt;
                else if (delta < 0) speed = 0;
                lastSize = f.Length;
                lastAt = now;
            }

            if (!announced)
            {
                announced = true;
                lastReport = now;
                Log($"[下载] 检测到更新包 {f.Name}，正在下载…");
            }
            else if ((now - lastReport).TotalSeconds >= 5)
            {
                lastReport = now;
                Log($"[下载] 已获取 {DownloadProbe.Human(f.Length)}" +
                    (speed > 0 ? $" · {DownloadProbe.Human((long)speed)}/s（官方源，速度取决于网络）" : ""));
            }
        }

        if (seen != null)
        {
            try
            {
                seen.Refresh();
                Log($"[下载] 结束：{seen.Name} 共 {DownloadProbe.Human(seen.Length)}" +
                    (seen.Length > 1048576 ? "，随后交给系统安装" : ""));
            }
            catch { }
        }
    }

    private void OnInstallFromFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 WSL 官方安装包",
            Filter = "WSL 安装包|*.msi;*.msix;*.msixbundle;*.appx;*.appxbundle|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        Log("准备安装：" + dlg.FileName);
        if (InstallService.StartElevatedInstall(dlg.FileName, out var msg))
        {
            Log(msg);
            Log("安装完成后请点击「重新检测」。");
        }
        else Log("启动安装失败：" + msg);
    }

    private void OnOpenStoreClick(object sender, RoutedEventArgs e)
    {
        InstallService.OpenStorePage();
        Log("已尝试打开 Microsoft Store 的 WSL 页面。");
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        InstallService.OpenReleasePage();
        Log("已尝试打开 WSL 官方发布页（GitHub Releases）。");
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TxtLog.Text)) Clipboard.SetText(TxtLog.Text);
            SetStatus("日志已复制到剪贴板", "OkBrush");
        }
        catch (Exception ex) { SetStatus("复制失败：" + ex.Message, "ErrBrush"); }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => TxtLog.Clear();

    // ══════════════════════════ 容器 ══════════════════════════
    private async void OnRefreshContainersClick(object sender, RoutedEventArgs e) => await RefreshContainersAsync();

    private void OnAllContainersChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _ = RefreshContainersAsync();
    }

    private async System.Threading.Tasks.Task RefreshContainersAsync(bool silent = false)
    {
        if (!_ready) return;

        if (!WslcService.Available)
        {
            if (!silent) SetStatus("wslc 尚未安装，请先在「环境与安装」页安装或升级 WSL", "WarnBrush");
            return;
        }

        bool all = ChkAllContainers.IsChecked == true;
        var (items, raw) = await WslcService.ListContainersAsync(all);
        var (stats, _) = await WslcService.StatsOnceAsync();

        var statMap = new Dictionary<string, ContainerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stats)
            if (!string.IsNullOrWhiteSpace(s.Name)) statMap[s.Name] = s;

        foreach (var c in items)
        {
            if (statMap.TryGetValue(c.Name, out var st)) c.ApplyStats(st);
            // 状态已在 ListContainersAsync 里按 JSON 的 State 字段判定过了
        }

        Ui(() =>
        {
            var selectedNames = new HashSet<string>(
                _containers.Where(c => c.IsSelected).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var keep = _selContainer?.Name;

            _containers.Clear();
            foreach (var i in items)
            {
                i.IsSelected = selectedNames.Contains(i.Name);
                _containers.Add(i);
            }

            if (keep != null)
            {
                var again = _containers.FirstOrDefault(c => c.Name == keep);
                if (again != null) GridContainers.SelectedItem = again;
            }
            UpdateSelectedCount();
        });

        if (!raw.Success && raw.ErrorMessage.Length > 0)
            Log("列举容器失败：" + One(raw.ErrorMessage));
    }

    private async System.Threading.Tasks.Task RefreshRealtimeStatsAsync()
    {
        if (!_ready || _busy || NavContainer.IsChecked != true) return;
        if (!WslcService.Available || _containers.Count == 0) return;

        try { _statsCts?.Cancel(); } catch { }
        _statsCts = new CancellationTokenSource();

        var (stats, _) = await WslcService.StatsOnceAsync(_statsCts.Token);
        if (stats.Count == 0) return;

        var map = new Dictionary<string, ContainerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stats)
            if (!string.IsNullOrWhiteSpace(s.Name)) map[s.Name] = s;

        Ui(() =>
        {
            foreach (var c in _containers)
            {
                if (map.TryGetValue(c.Name, out var st)) c.ApplyStats(st);
                else { c.Cpu = "—"; c.MemPct = "—"; c.MemDetail = ""; }
            }
        });
    }

    private void OnContainerSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _selContainer = GridContainers.SelectedItem as ContainerInfo;

    // ── 多选 ──
    private void OnSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool on = (sender as CheckBox)?.IsChecked == true;
        foreach (var c in _containers) c.IsSelected = on;
        UpdateSelectedCount();
    }

    private void OnRowCheckChanged(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateSelectedCount), DispatcherPriority.Background);

    private void UpdateSelectedCount()
    {
        int n = _containers.Count(c => c.IsSelected);
        TxtSelectedCount.Text = n == 0 ? "未选择容器" : $"已选择 {n} 个容器";
        if (Application.Current.TryFindResource(n > 0 ? "AccentBrush" : "MutedBrush") is Brush b)
            TxtSelectedCount.Foreground = b;
    }

    private List<ContainerInfo> SelectedContainers() => _containers.Where(c => c.IsSelected).ToList();

    // ── 批量 ──
    private async void OnBatchStartClick(object sender, RoutedEventArgs e) => await BatchAsync("start", "启动");
    private async void OnBatchStopClick(object sender, RoutedEventArgs e) => await BatchAsync("stop", "停止");
    private async void OnBatchKillClick(object sender, RoutedEventArgs e) => await BatchAsync("kill", "强制终止");
    private async void OnBatchRestartClick(object sender, RoutedEventArgs e) => await BatchAsync("restart", "重启");

    private async System.Threading.Tasks.Task BatchAsync(string action, string verb)
    {
        var sel = SelectedContainers();
        if (sel.Count == 0) { SetStatus("请先勾选要操作的容器", "WarnBrush"); return; }

        bool stopping = action == "stop" || action == "kill";
        bool starting = IsStartingAction(action);

        SetBusy(true, $"正在批量{verb} {sel.Count} 个容器…");
        Ui(() => TxtContainerLog.Clear());

        if (stopping)
            foreach (var c0 in sel) c0.State = ContainerState.Stopping;

        int ok = 0, died = 0;
        foreach (var c in sel)
        {
            var r = action switch
            {
                "stop" => await WslcService.StopAsync(c.Name),
                "kill" => await WslcService.KillAsync(c.Name),
                _ => await WslcService.ContainerActionAsync(action, c.Name),
            };

            // 同单容器路径：start / restart 的 exit=0 不代表容器在跑，必须逐一复查
            var st = r.Success && starting ? await VerifyRunningAsync(c.Name) : null;

            if (r.Success && DiedRightAfterStart(st))
            {
                died++;
                Log(ExplainImmediateExit(c.Name, st), TxtContainerLog);
            }
            else
            {
                Log($"{(r.Success ? "✓" : "✗")} {verb} {c.Name}" +
                    (r.Success ? "" : $"　{One(r.All)}"), TxtContainerLog);
                if (r.Success) ok++;
            }
        }

        SetBusy(false);
        SetStatus(died > 0
                ? $"批量{verb}完成：真正运行 {ok}、起后即退 {died}（共 {sel.Count}）"
                : $"批量{verb}完成：成功 {ok} / {sel.Count}",
            ok == sel.Count ? "OkBrush" : "WarnBrush");
        await RefreshContainersAsync();
    }

    private async void OnBatchRemoveClick(object sender, RoutedEventArgs e)
    {
        var sel = SelectedContainers();
        if (sel.Count == 0) { SetStatus("请先勾选要删除的容器", "WarnBrush"); return; }

        if (!AppDialog.Confirm(this, "批量删除容器",
                $"确定要删除选中的 {sel.Count} 个容器吗？\n该操作不可撤销。",
                okText: "删除", danger: true))
            return;

        SetBusy(true, $"正在删除 {sel.Count} 个容器…");
        Ui(() => TxtContainerLog.Clear());

        int ok = 0;
        foreach (var c in sel)
        {
            var r = await WslcService.RemoveAsync(c.Name, force: true);
            Log($"{(r.Success ? "✓" : "✗")} 删除 {c.Name}" +
                (r.Success ? "" : $"　{One(r.All)}"), TxtContainerLog);
            if (r.Success) ok++;
        }

        SetBusy(false);
        SetStatus($"批量删除完成：成功 {ok} / {sel.Count}", ok == sel.Count ? "OkBrush" : "WarnBrush");
        await RefreshContainersAsync();
    }

    // ── 单容器行内操作 ──
    private async void OnRowStartClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "start", "启动");
    private async void OnRowStopClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "stop", "停止");
    private async void OnRowKillClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "kill", "强制终止");
    private async void OnRowRestartClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "restart", "重启");

    private async System.Threading.Tasks.Task RowActionAsync(object sender, string action, string verb)
    {
        var c = CtxC(sender);
        if (c == null) return;

        if (action == "stop" || action == "kill")
        {
            // 立刻给出「停止中」反馈，避免操作期间状态看着没变化
            c.State = ContainerState.Stopping;
            SetStatus($"正在{verb} {c.Name}…", "WarnBrush");
        }

        SetBusy(true, $"{verb} {c.Name}…");
        Ui(() => TxtContainerLog.Clear());
        Log($"{verb}容器：{c.Name}");

        var r = action switch
        {
            "stop" => await WslcService.StopAsync(c.Name),
            "kill" => await WslcService.KillAsync(c.Name),
            _ => await WslcService.ContainerActionAsync(action, c.Name),
        };

        // start / restart 必须复查：exit=0 只代表「启动请求被接受」，
        // 不代表容器在跑（主进程一结束容器就停，命令照样返回 0）。
        var st = r.Success && IsStartingAction(action) ? await VerifyRunningAsync(c.Name) : null;
        SetBusy(false);

        if (r.Success && DiedRightAfterStart(st))
        {
            Log(ExplainImmediateExit(c.Name, st), TxtContainerLog);
            SetStatus($"{verb} {c.Name}：容器随即退出（退出码 {ExitCodeText(st)}）", "WarnBrush");
        }
        else
        {
            Log(r.Success ? $"完成：{verb} {c.Name}" : $"失败：{One(r.All)}", TxtContainerLog);
            SetStatus(r.Success ? $"{verb} {c.Name} 成功" : $"{verb} {c.Name} 失败",
                r.Success ? "OkBrush" : "ErrBrush");
        }

        await RefreshContainersAsync();
    }

    // ── 「启动成功」到底成没成 ────────────────────────────────────
    private static bool IsStartingAction(string action) => action == "start" || action == "restart";

    /// <summary>指令报成功、但复查发现容器并没在跑 —— 这才是「提示成功却还是已停止」的真身。</summary>
    private static bool DiedRightAfterStart(Services.ContainerRuntimeState st)
        => st != null && st.Queried && !st.Running;

    private static string ExitCodeText(Services.ContainerRuntimeState st)
        => st?.ExitCode.HasValue == true ? st.ExitCode.Value.ToString() : "未知";

    /// <summary>
    /// 启动 / 重启后**复查真实运行态**。
    ///
    /// 先给 350ms 沉降（避开 `start` 刚返回、状态还没落到 inspect 的窗口），
    /// 不通过再补两次、每次间隔 250ms —— 总上限约 1.1 秒。
    /// 之所以敢这么短：容器进入 running 是在**主进程一启动**时就发生的，
    /// 与「应用是否已经就绪」无关，所以正常启动第一轮就能查到。
    /// </summary>
    private static async Task<Services.ContainerRuntimeState> VerifyRunningAsync(string name)
    {
        await Task.Delay(350);
        var st = await WslcService.InspectStateAsync(name);
        for (int i = 0; i < 2 && st.Queried && !st.Running; i++)
        {
            await Task.Delay(250);
            st = await WslcService.InspectStateAsync(name);
        }
        return st;
    }

    /// <summary>
    /// 创建容器后复查 —— `wslc run -d` 的 exit=0 同样只是「指令被接受」。
    /// 容器身份：用户起的名字优先，没起名就从输出里抓它打印的容器 ID。
    /// 前台运行（未勾 -d）与 `--rm` 不复查：前者会阻塞、后者容器已自动销毁，都问不到。
    /// </summary>
    private static async Task<Services.ContainerRuntimeState> VerifyCreatedAsync(ContainerSpec spec, RunResult r)
    {
        if (!spec.Detach || spec.AutoRemove) return null;
        var id = !string.IsNullOrWhiteSpace(spec.Name) ? spec.Name.Trim() : WslcService.ParseCreatedId(r.All);
        return string.IsNullOrWhiteSpace(id) ? null : await VerifyRunningAsync(id);
    }

    /// <summary>容器「起后即退」时给出针对性解释（命令 / 退出码 / 怎么改）。</summary>
    private static string ExplainImmediateExit(string name, Services.ContainerRuntimeState st)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"✗ {name}：启动了但随即停止（退出码 {ExitCodeText(st)}）—— ");
        sb.Append("容器在主进程结束时就会停止，这条指令本身是成功的。");

        if (!string.IsNullOrWhiteSpace(st.Cmd)) sb.Append($"当前命令：{st.Cmd}。");

        sb.Append(WslcService.LooksLikeInteractiveShell(st.Cmd)
            ? "交互式 shell 没有终端会立刻读到 EOF 退出；新建容器时勾选「分配终端（-i -t）」即可常驻。"
            : "要让容器常驻，把命令换成不会自己结束的（例如 sleep infinity）。");

        sb.Append("详情见该行「日志」。");
        return sb.ToString();
    }

    /// <summary>名称列缩窄后，点一下弹完整名称并顺手复制，方便辨认长名字。</summary>
    private void OnNameClick(object sender, MouseButtonEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null || string.IsNullOrWhiteSpace(c.Name)) return;

        try { Clipboard.SetText(c.Name); } catch { }

        AppDialog.Info(this, "容器完整名称", c.Name + "\n\n（已复制到剪贴板）");
    }

    private void OnOpenDetailClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender) ?? _selContainer;
        if (c == null) return;
        new ContainerDetailDialog(c) { Owner = this }.ShowDialog();
    }

    private void OnMetricClick(object sender, MouseButtonEventArgs e) => OnOpenDetailClick(sender, e);

    /// <summary>日志改为弹出卡片。</summary>
    private void OnRowLogsClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null) return;
        new ContainerLogsDialog(c) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// 打开容器终端。
    /// 命令不写死 —— 容器里是 bash 还是 sh（或什么都没有）由镜像决定，
    /// 由 TerminalDialog 打开时实时探测后再让用户确认。
    /// </summary>
    private void OnRowShellClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null) return;

        var dlg = new TerminalDialog(c) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var shell = dlg.SelectedShell;
        if (string.IsNullOrWhiteSpace(shell)) return;

        if (TryStartTerminal(c.Name, shell))
            SetStatus($"已在终端中打开 {c.Name}（{shell}）", "OkBrush");
        else
            SetStatus("打开终端失败：未找到可用的终端程序（wt.exe / powershell.exe）", "ErrBrush");
    }

    /// <summary>
    /// 起一个外部终端跑 `wslc exec -it &lt;容器&gt; &lt;shell&gt;`。
    /// 优先 Windows Terminal（wt.exe）；Win10 或未装 Store 版时它不存在，
    /// 回退到 PowerShell（-NoExit 让窗口留在那里，不是一闪而过）。
    /// </summary>
    private static bool TryStartTerminal(string container, string shell)
    {
        var exe = WslcService.ExePath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        var inner = $"\"{exe}\" exec -it {container} {shell}";

        foreach (var (file, args) in new[]
                 {
                     ("wt.exe", inner),
                     ("powershell.exe", $"-NoExit -Command \"& '{exe}' exec -it {container} {shell}\""),
                 })
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = true
                });
                return true;
            }
            catch { /* 换下一个 */ }
        }
        return false;
    }

    private async void OnRowRemoveClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null) return;

        if (!AppDialog.Confirm(this, "删除容器",
                $"确定要删除容器「{c.Name}」吗？\n该操作不可撤销。",
                okText: "删除", danger: true))
            return;

        SetBusy(true, "正在删除…");
        var r = await WslcService.RemoveAsync(c.Name, force: true);
        SetBusy(false);
        SetStatus(r.Success ? $"已删除 {c.Name}" : "删除失败：" + One(r.All),
            r.Success ? "OkBrush" : "ErrBrush");
        await RefreshContainersAsync();
    }

    private void OnClearContainerLogClick(object sender, RoutedEventArgs e) => TxtContainerLog.Clear();

    private async void OnCreateContainerClick(object sender, RoutedEventArgs e)
    {
        var dlg = new CreateContainerDialog(_images) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        var spec = dlg.Result;
        Ui(() => TxtContainerLog.Clear());
        Log("新建容器：" + spec.PreviewCommandLine(), TxtContainerLog);

        SetBusy(true, "正在创建容器…");
        var r = await WslcService.RunAsync(spec, s => Log(s, TxtContainerLog));
        var st = r.Success ? await VerifyCreatedAsync(spec, r) : null;
        SetBusy(false);

        Log(r.Success ? "容器创建指令已执行（退出码 0）。" : $"创建失败（退出码 {r.ExitCode}）。", TxtContainerLog);
        foreach (var line in TableParser.Lines(r.All)) Log(line, TxtContainerLog);

        if (r.Success && DiedRightAfterStart(st))
        {
            Log(ExplainImmediateExit(!string.IsNullOrWhiteSpace(spec.Name) ? spec.Name : "新容器", st), TxtContainerLog);
            SetStatus($"容器已创建，但随即停止（退出码 {ExitCodeText(st)}）", "WarnBrush");
        }
        else SetStatus(r.Success ? "容器已创建" : "容器创建失败", r.Success ? "OkBrush" : "ErrBrush");

        await RefreshContainersAsync();
    }

    // ══════════════════════════ 镜像 ══════════════════════════
    private async void OnRefreshImagesClick(object sender, RoutedEventArgs e) => await RefreshImagesAsync();

    private async System.Threading.Tasks.Task RefreshImagesAsync(bool silent = false)
    {
        if (!_ready) return;
        if (!WslcService.Available)
        {
            if (!silent) SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        // 镜像清单与容器清单一起取：镜像列表要标出「被哪些容器占用」。
        // 必要性：wslc 删除被容器引用的镜像时**不报错、exit=0**，但只摘标签、
        // 镜像层不删、空间不释放（实测见 WslcService.InterpretImageRemove 注释）。
        var imgTask = WslcService.ListImagesAsync();
        var ctrTask = WslcService.ListContainersAsync(all: true);
        await System.Threading.Tasks.Task.WhenAll(imgTask, ctrTask);

        var (items, raw) = imgTask.Result;
        WslcService.AttachImageUsage(items, ctrTask.Result.Items);

        Ui(() =>
        {
            _images.Clear();
            foreach (var i in items) _images.Add(i);
        });

        if (!raw.Success && raw.ErrorMessage.Length > 0)
            Log("列举镜像失败：" + One(raw.ErrorMessage));
    }

    // ── 镜像检索（本地索引）──
    private void OnRepoQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRepoSearchClick(sender, e);
    }

    /// <summary>
    /// 检索 = 纯本地索引，同步瞬时返回。
    /// 不再"先干等远程 3 秒再回落"：wslc 没有官方 search 子命令，Docker Hub 的检索接口
    /// 在国内也不可达，那 3 秒等不到任何东西，只会让用户以为点了没反应。
    /// </summary>
    private void OnRepoSearchClick(object sender, RoutedEventArgs e)
    {
        var q = (TxtRepoQuery.Text ?? "").Trim();
        if (q.Length == 0) { SetStatus("请输入要检索的镜像关键字", "WarnBrush"); return; }

        ListSearchResults.ItemsSource = null;
        _searchResults.Clear();

        var local = ImageSearchService.SearchLocal(q);
        foreach (var it in local.Items) _searchResults.Add(it);
        ListSearchResults.ItemsSource = _searchResults;

        TxtSearchStatus.Text = local.Message;
        SetStatus(local.Items.Count > 0
                ? $"已列出 {local.Items.Count} 条（{ImageSearchService.IndexSourceText} v{ImageSearchService.IndexVersionText}）"
                : "未找到匹配项",
            local.Items.Count > 0 ? "OkBrush" : "WarnBrush");
    }

    private void OnRepoWebSearchClick(object sender, RoutedEventArgs e)
    {
        var q = (TxtRepoQuery.Text ?? "").Trim();
        ImageSearchService.OpenWebSearch(q, AppSettings.WebSearchUrlTemplate);
        SetStatus("已在浏览器中打开镜像搜索页", "InfoBrush");
    }

    private void OnSearchResultClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ImageSearchItem it)
        {
            TxtPullImage.Text = it.PullName;
            TxtPullImage.CaretIndex = it.PullName.Length;
            SetStatus("已填入镜像名：" + it.PullName, "InfoBrush");
        }
    }

    private void OnImageSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _selImage = GridImages.SelectedItem as ImageInfo;

    private void OnImageRowRemoveClick(object sender, RoutedEventArgs e)
    {
        var img = CtxI(sender) ?? _selImage;
        if (img == null) return;
        ConfirmRemoveImage(img);
    }

    /// <summary>输出区里的「删除选中镜像」按钮（走当前选中项）。</summary>
    private void OnImageRemoveClick(object sender, RoutedEventArgs e)
    {
        var img = _selImage;
        if (img == null) { SetStatus("请先在列表中选择一个镜像", "WarnBrush"); return; }
        ConfirmRemoveImage(img);
    }

    private void ConfirmRemoveImage(ImageInfo img)
    {
        string body;
        if (img.InUse)
        {
            var u = img.Usage;
            var who = new List<string>();
            if (u.RunningCount > 0) who.Add("运行中：" + string.Join("、", u.RunningNames));
            if (u.StoppedCount > 0) who.Add("已停止：" + string.Join("、", u.StoppedNames));

            body = $"镜像「{img.Reference}」正被 {u.Total} 个容器引用。\n\n" +
                   string.Join("\n", who) + "\n\n" +
                   "注意：这种情况下 wslc 只会「取消标记」——\n" +
                   "  · 不报错（退出码 0），看上去像删成功了\n" +
                   "  · 但镜像层不会删除，磁盘空间不会释放\n" +
                   "  · 镜像只是从列表里消失，这些容器仍能照常运行\n\n" +
                   "要真正回收空间，请先删除上面列出的容器。\n\n仍要继续吗？";
        }
        else
        {
            body = $"确定要删除镜像「{img.Reference}」吗？\n\n" +
                   "该镜像没有被任何容器引用，删除会真正回收镜像层。";
        }

        if (!AppDialog.Confirm(this, "删除镜像", body, okText: "删除", danger: true)) return;
        _ = RemoveImageAsync(img);
    }

    private async void OnTestMirrorsClick(object sender, RoutedEventArgs e)
    {
        if (!WslcService.Available) { SetStatus("wslc 尚未安装", "WarnBrush"); return; }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        // 待测清单 = 当前输入框里的地址（有就排在前面）∪ 预设清单。
        // 这样即使没有配置任何预设，也能直接测手上正在填的那个地址。
        var typed = WslcService.NormalizeMirrorPrefix(TxtMirror.Text);
        var targets = new List<string>();
        if (typed.Length > 0) targets.Add(typed);
        targets.AddRange(MirrorPresets().Select(WslcService.NormalizeMirrorPrefix));
        targets = targets.Where(x => x.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

        if (targets.Count == 0)
        {
            TxtMirrorTest.Text = "没有可检测的地址：请先在上面填写「加速前缀」。";
            SetStatus("请先填写一个加速前缀再检测", "WarnBrush");
            return;
        }

        SetBusy(true, "正在逐个实测加速地址…");
        var lines = new List<string>
        {
            "说明：检测方式是**真的拉取一次 alpine:latest**，可用即意味着该站能转发镜像；",
            "      测完 alpine:latest 会留在本地镜像列表里，不需要时可在镜像页删掉。"
        };
        int okCount = 0;
        string firstOk = null;

        foreach (var m in targets)
        {
            TxtMirrorTest.Text = string.Join("\r\n", lines) +
                                 $"\r\n… 正在检测 {m}";
            bool ok = await WslcService.TestMirrorAsync(m);
            lines.Add($"{(ok ? "✓ 可用  " : "✗ 不可用")}  {m}");
            TxtMirrorTest.Text = string.Join("\r\n", lines);
            if (ok) { okCount++; firstOk ??= m; }
        }

        lines.Add("");
        lines.Add(okCount > 0
            ? $"共 {okCount} / {targets.Count} 个可用。推荐：{firstOk}"
            : "全部不可用，可直接留空使用官方源（可能较慢）。");
        TxtMirrorTest.Text = string.Join("\r\n", lines);

        SetBusy(false);
        SetStatus($"加速地址检测完成：{okCount} / {targets.Count} 可用",
            okCount > 0 ? "OkBrush" : "WarnBrush");
    }

    /// <summary>
    /// 压缩 wslc 会话的虚拟磁盘（storage.vhdx）。
    /// 背景：删除镜像后 vhdx 只增不减（实测删 163MB 的 nginx 后磁盘 +0.0 MB），
    /// 需要手动 compact 才能真正回收。流程：警告 → 关 WSL → 提权 diskpart → 报告前后大小。
    /// </summary>
    private async void OnCompactDiskClick(object sender, RoutedEventArgs e)
    {
        var vhdx = WSLCManager.Helpers.DiskCompactor.FindStorageVhdx();
        if (vhdx == null) { SetStatus("未找到 wslc 会话的虚拟磁盘", "WarnBrush"); return; }

        var before = WSLCManager.Helpers.DiskCompactor.GetVhdxBytes(vhdx);
        var msg = "将对 wslc 会话的虚拟磁盘执行 diskpart compact：\n\n" + vhdx +
                  $"\n\n当前大小：{before / 1048576.0:N1} MB" +
                  "\n\n⚠ 流程：wsl --shutdown（所有 WSL / wslc 容器会停止）→ UAC 提权 → diskpart compact。" +
                  "\n\n⚠ 实测提醒（2026-10-10）：ext4 删除文件不清零，compact 只回收全零块，" +
                  "**对 wslc 会话的磁盘通常回收 0 字节**（fstrim 的 TRIM 也不传导）。" +
                  "此功能保留给确有需要的场景（如磁盘内做过全盘写零）。\n\n确定继续吗？";

        if (!AppDialog.Confirm(this, "压缩虚拟磁盘", msg, okText: "开始压缩", danger: false)) return;

        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }
        SetBusy(true, "正在压缩虚拟磁盘…");
        BtnCompact.IsEnabled = false;

        // 1) 关闭 WSL（含 wslc 会话），否则 vhdx 被占用无法压缩
        SetStatus("正在关闭 WSL…", "WarnBrush");
        var (ok, outText) = await System.Threading.Tasks.Task.Run(
            () => WSLCManager.Helpers.DiskCompactor.ShutdownWsl());
        if (!ok)
        {
            SetBusy(false); BtnCompact.IsEnabled = true;
            SetStatus("关闭 WSL 失败：" + outText, "ErrBrush");
            return;
        }
        await System.Threading.Tasks.Task.Delay(1500);   // 留时间让句柄完全释放

        // 2) 提权 diskpart（UAC 由用户确认）
        SetStatus("等待 UAC 授权（如未弹出请检查任务栏）…", "WarnBrush");
        var r = await System.Threading.Tasks.Task.Run(
            () => WSLCManager.Helpers.DiskCompactor.Compact(vhdx));

        // 3) 报告
        var after = WSLCManager.Helpers.DiskCompactor.GetVhdxBytes(vhdx);
        var saved = (before - after) / 1048576.0;
        Log($"── 压缩虚拟磁盘 ──", TxtImageLog);
        Log($"磁盘：{vhdx}", TxtImageLog);
        Log($"压缩前：{before / 1048576.0:N1} MB　压缩后：{after / 1048576.0:N1} MB　" +
            $"释放：{(saved > 0 ? saved.ToString("N1") + " MB" : "0 MB（无碎片可回收）")}", TxtImageLog);
        if (!string.IsNullOrWhiteSpace(r.Output))
            foreach (var l in r.Output.Split('\n'))
                if (l.Trim().Length > 0 && !l.Contains("Microsoft") && !l.Contains("版权"))
                    Log("  " + l.TrimEnd('\r'), TxtImageLog);

        SetBusy(false);
        BtnCompact.IsEnabled = true;

        if (r.Ok && saved > 0.05)
            SetStatus($"压缩完成，释放 {saved:N1} MB", "OkBrush");
        else if (r.Ok)
            SetStatus("压缩完成，但本磁盘没有可回收的碎片空间", "InfoBrush");
        else
            SetStatus("压缩失败：" + r.Error, "ErrBrush");
    }

    private void OnOpenWslcSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            WslcService.OpenSettingsFile();
            SetStatus("已用系统默认编辑器打开 wslc 设置文件", "InfoBrush");
        }
        catch (Exception ex)
        {
            SetStatus("打开设置文件失败：" + ex.Message, "ErrBrush");
        }
    }

    private async void OnImageRowOpenDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 放到后台线程取目录，避免同步调 wslc 卡住 UI
            var dir = await System.Threading.Tasks.Task.Run(() => WslcService.GetStorageDir());
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultWslcDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            SetStatus("已打开镜像存储目录：" + dir, "InfoBrush");
        }
        catch (Exception ex)
        {
            SetStatus("打开目录失败：" + ex.Message, "ErrBrush");
        }
    }

    private async System.Threading.Tasks.Task RemoveImageAsync(ImageInfo img)
    {
        SetBusy(true, "正在删除镜像…");
        Log($"执行：wslc image rm -f {img.Reference}", TxtImageLog);
        var r = await WslcService.RemoveImageAsync(img.Reference, force: true);
        SetBusy(false);

        foreach (var line in (r.All ?? "").Split('\n'))
            if (line.Trim().Length > 0) Log("  " + line.TrimEnd('\r'), TxtImageLog);

        // exit=0 不代表删掉了：wslc 可能只「取消标记」，必须看输出里有没有「已删除」行。
        switch (WslcService.InterpretImageRemove(r.Success, r.All))
        {
            case WslcService.ImageRemoveOutcome.Deleted:
                SetStatus($"已删除镜像 {img.Reference}（镜像层已删除）", "OkBrush");
                break;

            case WslcService.ImageRemoveOutcome.UntaggedOnly:
            {
                // ⚠ 不能一口咬定"被容器按住" —— 得实际查引用关系再说。
                // （2026-10-10 用户实测：当时一个容器都没有，却提示"被容器按住"）
                var holders = _containers
                    .Where(x => ImageRefMatches(x.Image, img.Reference))
                    .Select(x => x.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .ToList();

                if (holders.Count > 0)
                {
                    SetStatus($"只移除了标签：{img.Reference} 仍被 {holders.Count} 个容器引用", "WarnBrush");
                    Log($"  注意：没有「已删除」行，该镜像仍被容器引用 —— " +
                        $"先删掉这些容器才能回收：{string.Join("、", holders.Take(6))}" +
                        (holders.Count > 6 ? $" 等 {holders.Count} 个" : ""), TxtImageLog);
                }
                else
                {
                    SetStatus($"只移除了标签：{img.Reference} 的镜像层仍在", "WarnBrush");
                    Log("  注意：没有「已删除」行，但当前**没有容器引用它**。可能是该层仍被其它" +
                        "镜像当作父层共享，或 wslc 这次只做了取消标记。", TxtImageLog);
                }
                break;
            }

            default:
                SetStatus("删除失败：" + One(r.All), "ErrBrush");
                break;
        }

        // 无论层删没删，虚拟磁盘都不会自动缩小 —— 这才是"空间没回收"的常见原因，必须讲清楚。
        Log("  说明：镜像层删掉后，虚拟磁盘 storage.vhdx **不会自动缩小**，" +
            "在「资源管理器」里看文件大小仍不变（实测：删掉 163MB 的 nginx 后磁盘占用 +0.0 MB）。" +
            "要真正释放，需让 WSL 压缩该虚拟磁盘。", TxtImageLog);

        await RefreshImagesAsync(silent: true);
    }

    /// <summary>容器记录的镜像引用与待删镜像是否为同一个（容忍有无 tag、前缀差异）。</summary>
    private static bool ImageRefMatches(string containerImage, string reference)
    {
        if (string.IsNullOrWhiteSpace(containerImage) || string.IsNullOrWhiteSpace(reference)) return false;

        var a = containerImage.Trim();
        var b = reference.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        static string NoTag(string s)
        {
            var i = s.LastIndexOf(':');
            var j = s.LastIndexOf('/');
            return i > j ? s.Substring(0, i) : s;   // 只去掉 tag，保留 registry 端口里的冒号
        }
        return string.Equals(NoTag(a), NoTag(b), StringComparison.OrdinalIgnoreCase);
    }

    private void OnImageRunClick(object sender, RoutedEventArgs e)
    {
        var img = _selImage;
        if (img == null) { SetStatus("请先在列表中选择一个镜像", "WarnBrush"); return; }

        var dlg = new CreateContainerDialog(_images) { Owner = this };
        dlg.PresetImage(img.Reference);
        if (dlg.ShowDialog() != true) return;

        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        _ = RunCreatedAsync(dlg.Result);
    }

    private async System.Threading.Tasks.Task RunCreatedAsync(ContainerSpec spec)
    {
        Ui(() => TxtContainerLog.Clear());
        Log("新建容器：" + spec.PreviewCommandLine(), TxtContainerLog);

        SetBusy(true, "正在创建容器…");
        var r = await WslcService.RunAsync(spec, s => Log(s, TxtContainerLog));
        var st = r.Success ? await VerifyCreatedAsync(spec, r) : null;
        SetBusy(false);

        Log(r.Success ? "容器已创建。" : $"创建失败（退出码 {r.ExitCode}）。", TxtContainerLog);
        foreach (var line in TableParser.Lines(r.All)) Log(line, TxtContainerLog);

        if (r.Success && DiedRightAfterStart(st))
        {
            Log(ExplainImmediateExit(!string.IsNullOrWhiteSpace(spec.Name) ? spec.Name : "新容器", st), TxtContainerLog);
            SetStatus($"容器已创建，但随即停止（退出码 {ExitCodeText(st)}）", "WarnBrush");
        }
        else SetStatus(r.Success ? "容器已创建" : "容器创建失败", r.Success ? "OkBrush" : "ErrBrush");

        await RefreshContainersAsync();
    }

    private async void OnPullImageClick(object sender, RoutedEventArgs e)
    {
        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        var image = TxtPullImage.Text?.Trim();
        if (string.IsNullOrWhiteSpace(image)) { SetStatus("请先输入镜像名，例如 alpine:latest", "WarnBrush"); return; }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        Ui(() => TxtImageLog.Clear());
        var mirror = AppSettings.RegistryMirror?.Trim() ?? "";
        var actual = WslcService.ComposePullTarget(image, mirror);
        Log($"开始拉取镜像：{image}" +
            (string.Equals(actual, image, StringComparison.Ordinal)
                ? "（未配置加速前缀，直连官方源）"
                : $"　→　{actual}（加速源）"), TxtImageLog);

        SetBusy(true, "正在拉取镜像…");

        // wslc pull 在非终端环境不输出任何进度（实测 0 字节），
        // 所以自己在外面按网卡接收字节数采样，给用户一个实时速度。
        _pullCts = new CancellationTokenSource();
        StartPullMeter();
        Ui(() =>
        {
            BtnPullStop.Visibility = Visibility.Visible;
            BtnPullImage.IsEnabled = false;
        });

        var r = await WslcService.PullAsync(image, mirror, s => Log(s, TxtImageLog), 0, _pullCts.Token);

        StopPullMeter();
        Ui(() =>
        {
            BtnPullStop.Visibility = Visibility.Collapsed;
            BtnPullImage.IsEnabled = true;
        });
        SetBusy(false);

        if (r.Canceled)
        {
            var el = (DateTime.Now - _pullStart).TotalSeconds;
            Log($"已按你的操作停止拉取（已接收 {NetMeter.Human(_pullTotal)}，用时 {el:F0}s）。" +
                "这不是拉取失败，可以随时重新拉取。", TxtImageLog);
            SetStatus($"已停止拉取：{image}", "WarnBrush");
            TxtPullProgress.Text = "";
            await RefreshImagesAsync();
            return;
        }

        Log(r.Success ? $"拉取完成：{image}" : $"拉取失败（退出码 {r.ExitCode}）：{One(r.StdErr)}", TxtImageLog);
        if (!r.Success)
        {
            var tip = DiagnosePullFailure(r, mirror);
            if (tip.Length > 0) Log(tip, TxtImageLog);
        }

        SetStatus(r.Success ? $"镜像拉取完成：{image}" : "镜像拉取失败",
            r.Success ? "OkBrush" : "ErrBrush");

        await RefreshImagesAsync();
    }

    // ── 拉取进度采样（wslc 自己不给进度，只能外部量网速）──
    private CancellationTokenSource _pullCts;
    private DispatcherTimer _pullTimer;
    private long _pullPrevBytes;
    private long _pullStartBytes;
    private long _pullTotal;
    private double _pullSpeed;
    private DateTime _pullStart;

    private void StartPullMeter()
    {
        _pullStart = DateTime.Now;
        _pullStartBytes = NetMeter.TotalBytesReceived();
        _pullPrevBytes = _pullStartBytes;
        _pullTotal = 0;
        _pullSpeed = 0;

        Ui(() => TxtPullProgress.Text = "连接中…");

        _pullTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pullTimer.Tick -= OnPullTick;
        _pullTimer.Tick += OnPullTick;
        _pullTimer.Start();
    }

    private void StopPullMeter()
    {
        _pullTimer?.Stop();
        if (_pullTimer != null) _pullTimer.Tick -= OnPullTick;
    }

    private async void OnPullTick(object sender, EventArgs e)
    {
        long now = await System.Threading.Tasks.Task.Run(() => NetMeter.TotalBytesReceived());
        if (now < 0 || _pullPrevBytes < 0) return;

        long delta = now - _pullPrevBytes;
        if (delta < 0) delta = 0;                    // 计数器回绕
        _pullPrevBytes = now;
        _pullSpeed = delta;                          // 1 秒间隔 ≈ 字节/秒
        _pullTotal = now - _pullStartBytes;

        var el = (DateTime.Now - _pullStart).TotalSeconds;
        var s = _pullSpeed > 0
            ? $"↓ {NetMeter.Human((long)_pullSpeed)}/s"
            : "↓ 等待数据…";
        Ui(() => TxtPullProgress.Text = $"{s}　已接收 {NetMeter.Human(_pullTotal)}　用时 {el:F0}s");
    }

    private void OnStopPullClick(object sender, RoutedEventArgs e)
    {
        if (_pullCts == null || _pullCts.IsCancellationRequested) return;
        try { _pullCts.Cancel(); } catch { }
        SetStatus("正在中断拉取…", "WarnBrush");
    }

    /// <summary>把常见的 wslc pull 失败翻译成「怎么办」，而不是只甩一个退出码。</summary>
    private static string DiagnosePullFailure(RunResult r, string mirror)
    {
        var msg = ((r.StdErr ?? "") + " " + (r.StdOut ?? "")).ToLowerInvariant();

        if (msg.Contains("e_invalidarg") || msg.Contains("映像无效") || msg.Contains("invalid"))
            return "提示：镜像名格式不被接受。若已填加速前缀，请只填纯主机名（例如 mirror.example.com），不要带 https:// 。";

        if (msg.Contains("timeout") || msg.Contains("超时") || msg.Contains("deadline") ||
            msg.Contains("no such host") || msg.Contains("connection") || msg.Contains("exceeded"))
            return string.IsNullOrWhiteSpace(mirror)
                ? "提示：连接超时。官方源 registry-1.docker.io 在国内通常不可达 —— 建议到「设置与加速」填一个加速前缀后重试。"
                : "提示：连接超时。当前加速前缀可能已失效，可在「设置与加速」页点「检测加速地址可用性」换一个。";

        if (msg.Contains("not found") || msg.Contains("manifest unknown") || msg.Contains("不存在"))
            return "提示：仓库里没有这个镜像或标签，检查名字与 tag（例如 alpine:latest）。";

        return "";
    }

    private void OnClearImageLogClick(object sender, RoutedEventArgs e) => TxtImageLog.Clear();

    // ══════════════════════════ 设置 ══════════════════════════
    private void LoadSettingsIntoUi()
    {
        TxtMirror.Text = AppSettings.RegistryMirror ?? "";
        // 注意：这里绝不能同步调用 wslc（会在 UI 线程阻塞），只用推断出的默认路径
        TxtConfigPath.Text = "配置文件：" + AppSettings.Location +
                             "\r\nwslc 数据目录：" + DefaultWslcDir();
        UpdateIndexInfo();
        UpdateMirrorPreview();
        UpdateSelectedCount();
    }

    // ── 镜像索引（wslc 无 search 子命令，检索靠本地索引，索引要能更新）──

    private void UpdateIndexInfo()
    {
        TxtIndexInfo.Text = $"当前索引：{ImageSearchService.IndexSummary}\r\n" +
                            $"索引文件：{ImageSearchService.IndexFilePath}" +
                            (File.Exists(ImageSearchService.IndexFilePath) ? "（已存在）" : "（尚未生成）");
    }

    private void OnExportIndexClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出镜像索引",
            FileName = "image-index.json",
            DefaultExt = ".json",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var (ok, msg) = ImageSearchService.ExportIndex(dlg.FileName);
        TxtIndexMsg.Text = msg;
        SetStatus(msg, ok ? "OkBrush" : "ErrBrush");
    }

    private void OnImportIndexClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择镜像索引 JSON",
            CheckFileExists = true,
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var (ok, msg) = ImageSearchService.ImportIndex(dlg.FileName);
        TxtIndexMsg.Text = msg;
        SetStatus(msg, ok ? "OkBrush" : "ErrBrush");
        if (ok) UpdateIndexInfo();
    }

    private void OnResetIndexClick(object sender, RoutedEventArgs e)
    {
        ImageSearchService.ResetToBuiltIn();
        var msg = $"已恢复内置索引：{ImageSearchService.IndexSummary}";
        TxtIndexMsg.Text = msg;
        UpdateIndexInfo();
        SetStatus(msg, "OkBrush");
    }

    private static string DefaultWslcDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wslc");

    private void OnMirrorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateMirrorPreview();
    }

    private void UpdateMirrorPreview()
    {
        // 预览走与 PullAsync 相同的组合逻辑，保证「所见即所执行」
        // （也顺带体现两件事：填 https:// 前缀会被自动剥离成纯 host；
        //   官方镜像会自动补 library/ —— 部分镜像站对裸名直接回 denied）
        var pre = WslcService.NormalizeMirrorPrefix(TxtMirror.Text);
        var sample = WslcService.ComposePullTarget("alpine:latest", TxtMirror.Text);
        TxtMirrorPreview.Text = pre.Length == 0
            ? $"预览：拉取 alpine:latest  →  wslc pull {sample}（未配置加速，直连官方源）"
            : $"预览：拉取 alpine:latest  →  wslc pull {sample}（官方镜像自动补 library/）";

        // 填了带 scheme 的地址时给一句提醒（wslc 只认纯 host）
        var raw = (TxtMirror.Text ?? "").Trim();
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            TxtMirrorPreview.Text += "\n（https:// 会自动去掉，wslc 只接受纯主机名）";
    }

    private void OnMirrorPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string s)
        {
            TxtMirror.Text = s;
            SetStatus("已填入加速前缀：" + s + "（记得保存）", "InfoBrush");
        }
    }

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        AppSettings.RegistryMirror = (TxtMirror.Text ?? "").Trim();
        AppSettings.Save();

        _realtime.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(AppSettings.StatsIntervalMs, 1000, 60000));

        SetStatus("设置已保存（立即生效，无需重启）", "OkBrush");
        Log("设置已保存：" + AppSettings.Location);
        Log($"  加速前缀 = {(AppSettings.RegistryMirror.Length == 0 ? "(空，直连官方源)" : AppSettings.RegistryMirror)}");
        Log($"  镜像索引 = {ImageSearchService.IndexSummary}");
    }

    private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppSettings.Location);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("打开目录失败：" + ex.Message, "ErrBrush"); }
    }

    /// <summary>重启应用：拉起新实例并退出当前进程。</summary>
    private void OnRestartAppClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) { SetStatus("无法定位程序路径", "ErrBrush"); return; }

            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            _reallyExit = true;
            try { _tray?.Dispose(); } catch { }
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus("重启失败：" + ex.Message, "ErrBrush");
        }
    }
}
