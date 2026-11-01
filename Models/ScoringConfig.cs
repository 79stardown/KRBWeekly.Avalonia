namespace KRBWeekly.Models;

/// <summary>
/// settings.json 的完整模型：评分系数 + 搜索设置 + 筛选规则。
/// （类名沿用 2.0 的 ScoringConfig，避免改动既有配置文件的字段名与调用点）
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

    // ===== 自动计算：搜索 =====

    /// <summary>
    /// 搜索标签（关键词）。默认 "KARDS金曲"。
    /// 实际会跑两轮：轮1 加引号做精确短语；轮2 不加引号做分词兜底。
    /// </summary>
    public string SearchKeyword { get; set; } = "KARDS金曲";

    // ===== 自动计算：筛选规则集 =====

    /// <summary>
    /// 自动筛选规则集。旧版 settings.json 没有这一项时反序列化会保留默认值
    /// （即 A/B/C/D 四级过滤表），因此升级后行为不变。
    /// </summary>
    public FilterRuleSet FilterRules { get; set; } = FilterRuleSet.CreateDefault();
}
