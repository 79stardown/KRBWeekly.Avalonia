using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.Styling;

namespace KRBWeekly.Helpers;

/// <summary>
/// 外观（深浅色模式 + 主题色）的统一入口。
/// 启动时和设置页保存后都走这里 —— FluentAvaloniaTheme 没有静态单例，
/// 查找逻辑只能写一份，别在两个地方各写一套。
/// </summary>
public static class ThemeService
{
    /// <summary>默认强调蓝（2.0 界面原本硬编码的那个蓝）</summary>
    public const string DefaultAccentHex = "#FF0078D4";

    private static FluentAvaloniaTheme? CurrentTheme =>
        Application.Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();

    /// <summary>
    /// 应用主题色。空值/无效值回落到默认蓝而非系统色：本工具只在浅色/深色两态下设计，
    /// 用固定默认值保证换台机器观感一致。
    /// 设置后 FluentAvalonia 会自动刷新 SystemAccentColor 等一批资源，
    /// 界面上 {DynamicResource SystemAccentColor} 的引用即时更新，无需重启。
    /// </summary>
    public static void ApplyAccent(string? colorHex)
    {
        var theme = CurrentTheme;
        if (theme == null) return;

        if (string.IsNullOrWhiteSpace(colorHex) || !Color.TryParse(colorHex, out var c))
            c = Color.Parse(DefaultAccentHex);

        theme.CustomAccentColor = c;
    }

    /// <summary>
    /// 应用深浅色模式。PreferSystemTheme 必须显式置 false，
    /// 否则 FluentAvaloniaTheme 会按系统主题覆盖 Application.RequestedThemeVariant。
    /// </summary>
    public static void ApplyThemeMode(string? mode)
    {
        var theme = CurrentTheme;
        if (theme != null) theme.PreferSystemTheme = false;

        if (Application.Current is { } app)
            app.RequestedThemeVariant = IsDark(mode) ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public static bool IsDark(string? mode)
        => string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase);
}
