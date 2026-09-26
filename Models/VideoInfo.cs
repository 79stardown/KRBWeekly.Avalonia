namespace KRBWeekly.Models;

/// <summary>
/// Bilibili 视频完整信息（包含原始数据 + 增量 + 计分结果）
/// </summary>
public class VideoInfo
{
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public long View { get; set; }
    public long Like { get; set; }
    public long Coin { get; set; }
    public long Favorite { get; set; }
    public DateTime PubDate { get; set; }

    // 搜索阶段信息（用于详情预过滤）
    /// <summary>搜索结果自带的播放量，解析失败为 -1</summary>
    public long PlayFromSearch { get; set; } = -1;

    // 本周增量
    public long DeltaView { get; set; }
    public long DeltaLike { get; set; }
    public long DeltaCoin { get; set; }
    public long DeltaFavorite { get; set; }

    /// <summary>新上榜折算系数（>14 天的首进榜视频按日均折算，默认 1 = 不折算）</summary>
    public double DeltaFactor { get; set; } = 1.0;

    // 计分结果
    public double TotalScore { get; set; }
    public double PlayScore { get; set; }
    public double FavScore { get; set; }
    public double LikeCoinScore { get; set; }
    public double CoefA { get; set; }
    public double CoefB { get; set; }
    public double CoefC { get; set; }

    // 排名与状态
    public int Rank { get; set; }
    public int? LastRank { get; set; }
    public string RankChange { get; set; } = "";
    public string RankChangeShort { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsNew { get; set; }
    public bool IsWhitelist { get; set; }

    // 过滤信息
    public double DaysDiff { get; set; }
    public string FilterReason { get; set; } = "";

    // 封面
    public string CoverUrl { get; set; } = "";
}
