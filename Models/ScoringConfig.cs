namespace KRBWeekly.Models;

/// <summary>
/// 计分修正系数配置（可修改，保存到 Data/settings.json）
/// </summary>
public class ScoringConfig
{
    // 修正A: max(1.4 - days/40, 1.0)
    public double AMax { get; set; } = 1.4;
    public double ADecay { get; set; } = 40.0;
    public double AMin { get; set; } = 1.0;

    // 修正B: min(fav/view * 500, 40.0)
    public double BMultiplier { get; set; } = 500.0;
    public double BMax { get; set; } = 40.0;

    // 修正C: min(min(like,coin)/view * 250, 50.0)
    public double CMultiplier { get; set; } = 250.0;
    public double CMax { get; set; } = 50.0;

    // 计算方式："Weighted" = 修正系数（A/B/C 系数，默认）；"Simple" = 单纯相加（四项增量直接求和）
    public string CalcMode { get; set; } = "Weighted";

    // 主题色："#AARRGGBB"；空字符串 = 默认蓝 #FF0078D4
    public string AccentColor { get; set; } = "";

    // 主题模式："Light"（默认）；"Dark"
    public string ThemeMode { get; set; } = "Light";
}
