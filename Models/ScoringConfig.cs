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
}
