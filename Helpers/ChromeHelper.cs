using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WSLCManager.Helpers;

/// <summary>
/// 无边框窗口的拖拽 / 缩放 / 最大化修正。
/// WindowStyle=None + ResizeMode=CanResize 时 WPF 不会自己处理边缘缩放，
/// 必须自己回 WM_NCHITTEST；最大化还要回 WM_GETMINMAXINFO 否则会盖住任务栏。
/// </summary>
public static class ChromeHelper
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    /// <summary>
    /// 让对话框相对 Owner 真正居中。
    /// 不用 WindowStartupLocation=CenterOwner 的原因：当对话框比主窗口还高时，
    /// 系统只保证「不超出屏幕」，结果看起来就是整体偏下。
    /// </summary>
    public static void CenterOnOwner(Window dlg)
    {
        try
        {
            var owner = dlg.Owner;
            double w = dlg.Width, h = dlg.Height;
            if (double.IsNaN(w) || w <= 0) w = dlg.ActualWidth;
            if (double.IsNaN(h) || h <= 0) h = dlg.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var wa = SystemParameters.WorkArea;
            double left, top;

            if (owner != null && owner.ActualWidth > 0 && owner.ActualHeight > 0)
            {
                left = owner.Left + (owner.ActualWidth - w) / 2;
                top = owner.Top + (owner.ActualHeight - h) / 2;
            }
            else
            {
                left = wa.Left + (wa.Width - w) / 2;
                top = wa.Top + (wa.Height - h) / 2;
            }

            // 约束在显示器工作区内，避免跑出屏幕
            left = Math.Max(wa.Left + 8, Math.Min(left, wa.Right - w - 8));
            top = Math.Max(wa.Top + 8, Math.Min(top, wa.Bottom - h - 8));

            dlg.Left = left;
            dlg.Top = top;
        }
        catch { }
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_NCRENDERING_POLICY = 2;
    /// <summary>DWMNCRP_DISABLED：DWM 不渲染窗口的非客户区（顶部那条色带就来自它）。</summary>
    private const int DWMNCRP_DISABLED = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 给窗口装上自绘外壳。
    /// </summary>
    /// <param name="w">目标窗口（需 WindowStyle=None, ResizeMode=CanResize）</param>
    /// <param name="captionHeight">标题栏高度（DIP），此区域内按标题栏处理</param>
    /// <param name="interactive">标题栏里需要可点击的元素（按钮等）</param>
    /// <param name="resizeBorder">边缘缩放命中宽度（DIP）</param>
    public static void Attach(Window w, double captionHeight, IEnumerable<FrameworkElement> interactive,
                              double resizeBorder = 6)
    {
        var list = new List<FrameworkElement>(interactive ?? Array.Empty<FrameworkElement>());

        w.SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 直角窗口：Win11 默认会给窗口加圆角，四角会露出后面的桌面，
            // 看起来像"缺角"。用 DWMWA_WCP_DONOTROUND(1) 关掉。
            int round = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            // 标题栏条带 / 窗口边线的颜色与深浅标志：**必须跟随主题**。
            // 否则深色主题下窗口顶部会留一条浅色带（DWM 仍按浅色在画）。
            ApplyDwmTheme(w, ThemeManager.IsDark);

            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == WM_GETMINMAXINFO)
                {
                    FixMaximizedBounds(w, h, lp);
                    handled = true;
                    return IntPtr.Zero;
                }
                if (msg == WM_NCHITTEST)
                {
                    handled = true;
                    return new IntPtr(HitTest(w, lp, captionHeight, list, resizeBorder));
                }
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>
    /// 让 DWM 绘制的非客户区跟随主题：深浅标志 + 标题栏 / 窗口边线颜色。
    ///
    /// 必须在**句柄创建之后**调用；换主题时对每个已打开的窗口再调一次
    /// （<see cref="ThemeManager.Apply"/> 负责遍历）。
    /// 注：试过 DWMWA_NCRENDERING_POLICY=DISABLED，那样 DWM 干脆不画这段，
    /// PrintWindow 会抓到未初始化的灰色，反而更明显，故不用；
    /// 真正消除顶部色带靠的是 WindowChrome（见 MainWindow.xaml）。
    /// </summary>
    public static void ApplyDwmTheme(Window w, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            int flag = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int));

            int chrome = ToColorRef(ThemeManager.WindowBaseColor());
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref chrome, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref chrome, sizeof(int));
        }
        catch { /* 旧版 Windows 上这些属性可能不存在，忽略即可 */ }
    }

    /// <summary>Color → Win32 COLORREF（0x00BBGGRR —— 顺序是 BGR，不是 RGB）。</summary>
    private static int ToColorRef(Color c) => (c.B << 16) | (c.G << 8) | c.R;

    private static int HitTest(Window w, IntPtr lParam, double captionHeight,
                               List<FrameworkElement> interactive, double border)
    {
        long v = lParam.ToInt64();
        int sx = unchecked((short)(v & 0xFFFF));
        int sy = unchecked((short)((v >> 16) & 0xFFFF));

        // 物理像素 → DIP
        var src = PresentationSource.FromVisual(w);
        double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var tl = w.PointToScreen(new Point(0, 0));
        var br = w.PointToScreen(new Point(w.ActualWidth, w.ActualHeight));
        double winW = br.X - tl.X;
        double winH = br.Y - tl.Y;
        double px = sx - tl.X;
        double py = sy - tl.Y;

        bool atLeft = px <= border * dpiX;
        bool atRight = px >= winW - border * dpiX;
        bool atTop = py <= border * dpiY;
        bool atBottom = py >= winH - border * dpiY;

        if (w.WindowState != WindowState.Maximized)
        {
            if (atTop && atLeft) return HTTOPLEFT;
            if (atTop && atRight) return HTTOPRIGHT;
            if (atBottom && atLeft) return HTBOTTOMLEFT;
            if (atBottom && atRight) return HTBOTTOMRIGHT;
            if (atLeft) return HTLEFT;
            if (atRight) return HTRIGHT;
            if (atTop) return HTTOP;
            if (atBottom) return HTBOTTOM;
        }

        // 标题栏区域
        if (py <= captionHeight * dpiY + 0.5)
        {
            // 落在可点元素上 → 交给 WPF
            foreach (var el in interactive)
            {
                if (el == null || !el.IsVisible) continue;
                var a = el.PointToScreen(new Point(0, 0));
                var b = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                if (sx >= a.X && sx <= b.X && sy >= a.Y && sy <= b.Y) return HTCLIENT;
            }
            return HTCAPTION;
        }

        return HTCLIENT;
    }

    private static void FixMaximizedBounds(Window w, IntPtr hwnd, IntPtr lParam)
    {
        var mi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var moni = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref moni)) return;

        int workW = moni.rcWork.right - moni.rcWork.left;
        int workH = moni.rcWork.bottom - moni.rcWork.top;

        var src = PresentationSource.FromVisual(w);
        double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // 最小尺寸必须 clamp 到工作区，否则高 DPI 下最大化会被 MinWidth 撑爆
        int minW = (int)Math.Min(w.MinWidth * dpiX, workW);
        int minH = (int)Math.Min(w.MinHeight * dpiY, workH);
        mi.ptMinTrackSize.x = Math.Max(minW, 64);
        mi.ptMinTrackSize.y = Math.Max(minH, 64);

        // 最大化时对齐到工作区（否则无边框窗口会盖住任务栏）
        mi.ptMaxPosition.x = moni.rcWork.left - moni.rcMonitor.left;
        mi.ptMaxPosition.y = moni.rcWork.top - moni.rcMonitor.top;
        mi.ptMaxSize.x = workW;
        mi.ptMaxSize.y = workH;

        Marshal.StructureToPtr(mi, lParam, false);
    }
}
