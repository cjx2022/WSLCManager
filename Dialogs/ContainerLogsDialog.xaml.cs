using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WSLCManager.Helpers;
using WSLCManager.Models;
using WSLCManager.Services;

namespace WSLCManager.Dialogs;

/// <summary>容器运行日志（弹窗卡片）。支持勾选「跟踪」自动刷新。</summary>
public partial class ContainerLogsDialog : Window
{
    private readonly ContainerInfo _c;
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;

    public ContainerLogsDialog(ContainerInfo c)
    {
        InitializeComponent();
        _c = c;
        TxtTitle.Text = string.IsNullOrWhiteSpace(c.Name) ? c.ShortId : c.Name;

        _follow.Tick += async (s, e) => await ReloadAsync(silent: true);

        Loaded += async (s, e) =>
        {
            ChromeHelper.CenterOnOwner(this);
            await ReloadAsync();
        };

        Closed += (s, e) => _follow.Stop();
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnReloadClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    /// <summary>勾选「跟踪」后每 2 秒自动拉一次日志并滚到最新。</summary>
    private void OnFollowChanged(object sender, RoutedEventArgs e)
    {
        if (ChkFollow.IsChecked == true)
        {
            _follow.Start();
            TxtHint.Text = "跟踪中：每 2 秒自动刷新";
            _ = ReloadAsync(silent: true);
        }
        else
        {
            _follow.Stop();
        }
    }

    private async System.Threading.Tasks.Task ReloadAsync(bool silent = false)
    {
        if (_busy) return;
        if (!WslcService.Available)
        {
            TxtLogs.Text = "wslc 未安装。";
            return;
        }

        int tail = 300;
        int.TryParse(TxtTail.Text?.Trim(), out tail);
        if (tail <= 0) tail = 300;

        _busy = true;
        if (!silent) TxtHint.Text = "正在读取…";

        RunResult r;
        try { r = await WslcService.LogsAsync(_c.Name, tail); }
        finally { _busy = false; }

        TxtLogs.Text = string.IsNullOrWhiteSpace(r.All)
            ? "(暂无日志输出)"
            : string.Join(Environment.NewLine, TableParser.Lines(r.All));
        TxtLogs.ScrollToEnd();

        if (!silent || ChkFollow.IsChecked != true)
            TxtHint.Text = r.Success
                ? $"已加载最近 {tail} 行　·　{DateTime.Now:HH:mm:ss}"
                : "读取失败：" + (r.ErrorMessage ?? r.StdErr ?? "");
        else
            TxtHint.Text = $"跟踪中　·　{DateTime.Now:HH:mm:ss}";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TxtLogs.Text)) Clipboard.SetText(TxtLogs.Text);
            TxtHint.Text = "已复制到剪贴板";
        }
        catch (Exception ex) { TxtHint.Text = "复制失败：" + ex.Message; }
    }
}
