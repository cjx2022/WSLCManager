using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace WSLCManager.Helpers;

/// <summary>
/// 系统托盘图标。用 WinForms 的 NotifyIcon（WPF 没有内置托盘 API），
/// 只用于「关闭后最小化到后台」这一个用途。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _ni;
    private bool _disposed;

    /// <summary>左键单击 / 双击 / 菜单「显示主界面」。</summary>
    public event Action ShowRequested;

    /// <summary>菜单「退出」。</summary>
    public event Action ExitRequested;

    public TrayIcon(string tooltip)
    {
        _ni = new NotifyIcon
        {
            Visible = true,
            Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip,
            Icon = LoadAppIcon(),
        };

        _ni.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowRequested?.Invoke();
        };
        _ni.DoubleClick += (s, e) => ShowRequested?.Invoke();

        var menu = new ContextMenuStrip();
        var miShow = new ToolStripMenuItem("显示主界面");
        miShow.Click += (s, e) => ShowRequested?.Invoke();
        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (s, e) => ExitRequested?.Invoke();
        menu.Items.Add(miShow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);
        _ni.ContextMenuStrip = menu;
    }

    /// <summary>取自身 exe 关联的图标（即 app.ico）。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                var ic = Icon.ExtractAssociatedIcon(exe);
                if (ic != null) return ic;
            }
        }
        catch { }

        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (res != null) return new Icon(res.Stream);
        }
        catch { }

        return SystemIcons.Application;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _ni.BalloonTipTitle = title;
            _ni.BalloonTipText = text;
            _ni.ShowBalloonTip(2500);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _ni.Visible = false;
            _ni.Dispose();
        }
        catch { }
    }
}
