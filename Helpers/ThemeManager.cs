using System;
using System.Windows;
using System.Windows.Media;

namespace WSLCManager.Helpers;

/// <summary>
/// 浅色 / 深色主题切换。
///
/// 机制：两份调色板（<c>Themes/Palette.Light.xaml</c> / <c>Palette.Dark.xaml</c>）key 完全一致，
/// 切换 = 把 <c>Application.Resources.MergedDictionaries[0]</c> 整本换掉。
/// 之所以能"热换"，是因为所有引用都走 <c>{DynamicResource}</c> ——
/// <c>{StaticResource}</c> 在解析那一刻就把对象定死了，换字典它不会更新。
///
/// ⚠ 约定：App.xaml 里 **调色板必须排第 0 本**，Styles.xaml 在后。
///   这里按索引替换而不是按名字找（MergedDictionaries 本身不带名字），
///   顺序写错就会换错字典 —— 探针第 24 节会核对两份调色板的 key 集合。
/// </summary>
public static class ThemeManager
{
    private const string LightSource = "Themes/Palette.Light.xaml";
    private const string DarkSource = "Themes/Palette.Dark.xaml";

    /// <summary>当前是否深色。取值由 <see cref="Apply"/> 决定（App.OnStartup 里按配置初始化）。</summary>
    public static bool IsDark { get; private set; }

    public static string CurrentName => IsDark ? "深色" : "浅色";

    public static void Apply(bool dark)
    {
        IsDark = dark;
        try
        {
            var app = Application.Current;
            if (app == null) return;

            var dict = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkSource : LightSource, UriKind.Relative)
            };

            var md = app.Resources.MergedDictionaries;
            if (md.Count == 0) md.Add(dict);
            else md[0] = dict;                 // 约定：第 0 本永远是调色板

            // 无边框窗口的非客户区由 DWM 画，不经过资源系统 —— 得逐个通知，
            // 否则深色主题下窗口顶部留一条按浅色画的色带。
            foreach (Window w in app.Windows) ChromeHelper.ApplyDwmTheme(w, dark);
        }
        catch { /* 换肤失败就当没换：宁可样式旧一点，也不能让界面挂掉 */ }
    }

    public static bool Toggle()
    {
        Apply(!IsDark);
        return IsDark;
    }

    /// <summary>窗口渐变起点色。DWM 标题栏要对齐它，否则顶部会有色带。</summary>
    public static Color WindowBaseColor()
    {
        try
        {
            if (Application.Current?.Resources["WindowGradient"] is LinearGradientBrush g &&
                g.GradientStops.Count > 0)
                return g.GradientStops[0].Color;
        }
        catch { }
        return IsDark ? Color.FromRgb(0x1A, 0x1D, 0x23) : Color.FromRgb(0xFB, 0xFC, 0xFD);
    }
}
