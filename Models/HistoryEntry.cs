namespace KRBWeekly.Models;

/// <summary>
/// history.json 中的单条记录（与 V3 兼容）
/// </summary>
public class HistoryEntry
{
    public long View { get; set; }
    public long Like { get; set; }
    public long Coin { get; set; }
    public long Favorite { get; set; }
    public int LastRank { get; set; }
}
