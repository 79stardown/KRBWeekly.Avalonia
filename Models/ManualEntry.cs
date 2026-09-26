namespace KRBWeekly.Models;

/// <summary>
/// 手动录入的数值组（输入-计算模式，独立成榜，不与 B 站自动数据混合）
/// </summary>
public class ManualEntry
{
    /// <summary>编号（自动分配，删除后不复用）</summary>
    public int Number { get; set; }

    /// <summary>代号（自由文本，如歌名；留空则显示「第N组」）</summary>
    public string Code { get; set; } = "";

    public long DeltaView { get; set; }
    public long DeltaLike { get; set; }
    public long DeltaCoin { get; set; }
    public long DeltaFavorite { get; set; }

    /// <summary>发布天数（可选；null 表示按「不折算」口径，A 系数取 1.0）</summary>
    public double? PublishDays { get; set; }

    // 以下为录入/编辑时按当时的 ScoringConfig 算出的快照，随条目持久化（不随系数改动回溯重算）
    public double TotalScore { get; set; }
    public double CoefA { get; set; }
    public double CoefB { get; set; }
    public double CoefC { get; set; }
    public double PlayScore { get; set; }
    public double FavScore { get; set; }
    public double LikeCoinScore { get; set; }

    /// <summary>列表显示名：代号为空时回退「第N组」</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Code) ? $"第{Number}组" : Code;

    /// <summary>发布天数显示</summary>
    public string PublishDaysStr => PublishDays is null or <= 0
        ? "默认（不折算）"
        : $"{PublishDays.Value:F1} 天";
}
