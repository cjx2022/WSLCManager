using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WSLCManager.Helpers;

namespace WSLCManager.Dialogs;

/// <summary>
/// 自绘的提示 / 确认对话框。
/// 替代系统自带 MessageBox —— 后者是 Windows 原生样式（方角、系统字、蓝色按钮），
/// 跟本程序的圆角浅色卡片风格完全对不上。
/// </summary>
public partial class AppDialog : Window
{
    private AppDialog()
    {
        InitializeComponent();
        Loaded += (s, e) => ChromeHelper.CenterOnOwner(this);
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Finish(true);
    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(false);

    /// <summary>
    /// 收口关闭。
    /// ⚠ <c>DialogResult</c> 只能在 <c>ShowDialog()</c>（模态）下赋值：验收参数
    /// <c>--dialog=…</c> 配 <c>--screenshot=</c> 时走的是非模态 <c>Show()</c>，
    /// 那里赋值会抛 InvalidOperationException —— 表现就是「点一下按钮就报错」。
    /// </summary>
    private void Finish(bool ok)
    {
        try { DialogResult = ok; }
        catch (InvalidOperationException) { /* 非模态显示：没有 DialogResult 可用，直接关掉 */ }
        Close();
    }

    private void Setup(string title, string message, string okText,
                       bool danger, bool showCancel, string cancelText)
    {
        TxtTitle.Text = title;
        TxtMessage.Text = message;
        BtnOk.Content = okText;
        BtnCancel.Content = cancelText;
        BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        // 没有「取消」时，Esc 也应该能关掉（否则信息框只能用鼠标点按钮）
        BtnOk.IsCancel = !showCancel;

        if (danger)
        {
            IconBadge.Background = (Brush)Application.Current.FindResource("ErrSoftBrush");
            IconGlyph.Foreground = (Brush)Application.Current.FindResource("ErrBrush");
            IconGlyph.Text = "\uE7BA";              // 警告三角
            BtnOk.Style = (Style)Application.Current.FindResource("DangerPrimaryButton");
            BtnOk.Padding = new Thickness(20, 8, 20, 8);
            BtnOk.MinWidth = 88;
        }
        else
        {
            IconBadge.Background = (Brush)Application.Current.FindResource("AccentSoftBrush");
            IconGlyph.Foreground = (Brush)Application.Current.FindResource("AccentBrush");
            IconGlyph.Text = "\uE946";              // 信息
        }
    }

    /// <summary>构造但不显示（供 --dialog=confirm 这类验收入口使用）。</summary>
    internal static AppDialog Build(string title, string message, string okText,
        bool danger, bool showCancel = true, string cancelText = "取消")
    {
        var d = new AppDialog();
        d.Setup(title, message, okText, danger, showCancel, cancelText);
        return d;
    }

    /// <summary>纯提示（只有「知道了」）。</summary>
    public static void Info(Window owner, string title, string message, string okText = "知道了")
    {
        var d = new AppDialog { Owner = owner };
        d.Setup(title, message, okText, danger: false, showCancel: false, cancelText: "");
        d.ShowDialog();
    }

    /// <summary>确认框；danger=true 时确定按钮为红色（删除类操作）。</summary>
    public static bool Confirm(Window owner, string title, string message,
        string okText = "确定", bool danger = false, string cancelText = "取消")
    {
        var d = new AppDialog { Owner = owner };
        d.Setup(title, message, okText, danger, showCancel: true, cancelText);
        return d.ShowDialog() == true;
    }
}
