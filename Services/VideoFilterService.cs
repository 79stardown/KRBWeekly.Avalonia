namespace KRBWeekly.Services;

/// <summary>
/// 视频过滤服务 —— 与 V3 4 级过滤规则完全一致
///
/// 条件1: 1天 &lt; days &lt; 7天 (无条件)
/// 条件2: 7天 ≤ days &lt; 30天 AND views &gt; 3000
/// 条件3: 30天 ≤ days &lt; 180天 AND views &gt; 50000
/// 条件4: days ≥ 180天 AND views &gt; 100000
/// 白名单直接通过，黑名单直接拒绝
/// </summary>
public static class VideoFilterService
{
    public static (bool Qualified, double DaysDiff, string Reason) CheckEligible(
        DateTime pubDate, long totalViews,
        bool isWhitelisted, bool isBlacklisted,
        DateTime now)
    {
        // 黑名单直接拒绝
        if (isBlacklisted)
        {
            var bd = (now - pubDate).TotalDays;
            return (false, bd, "黑名单");
        }

        var daysDiff = (now - pubDate).TotalDays;

        // 白名单直接通过（但仍计算 daysDiff 以修复 V3 Bug）
        if (isWhitelisted)
        {
            return (true, daysDiff, "白名单");
        }

        // 条件1：1天 < days < 7天（无播放量要求）
        if (1 < daysDiff && daysDiff < 7)
        {
            return (true, daysDiff, $"条件1 (发布{daysDiff:F1}天)");
        }

        // 条件2：7天 ≤ days < 30天，播放 > 3000
        if (7 <= daysDiff && daysDiff < 30 && totalViews > 3000)
        {
            return (true, daysDiff, $"条件2 (发布{daysDiff:F1}天, 播放{totalViews})");
        }

        // 条件3：30天 ≤ days < 180天，播放 > 50000
        if (30 <= daysDiff && daysDiff < 180 && totalViews > 50000)
        {
            return (true, daysDiff, $"条件3 (发布{daysDiff:F1}天, 播放{totalViews})");
        }

        // 条件4：days ≥ 180天，播放 > 100000
        if (daysDiff >= 180 && totalViews > 100000)
        {
            return (true, daysDiff, $"条件4 (发布{daysDiff:F1}天, 播放{totalViews})");
        }

        return (false, daysDiff, $"不满足条件 (发布{daysDiff:F1}天, 播放{totalViews})");
    }

    /// <summary>
    /// 详情预过滤 —— 用搜索结果自带的播放量做宽松初筛（阈值为正式过滤条件的 80%），
    /// 减少逐视频详情请求量。play 解析不到（-1）或发布天数 < 1 以外的边界一律保守放行。
    /// 预过滤只会跳过「必然过不了正式过滤」的视频，不影响正确性。
    /// </summary>
    public static bool ShouldFetchDetail(
        DateTime pubDate, long playFromSearch,
        bool isWhitelisted, bool isBlacklisted,
        DateTime now)
    {
        if (isBlacklisted) return false;   // 黑名单无需详情数据
        if (isWhitelisted) return true;    // 白名单强制保留
        if (playFromSearch < 0) return true;         // 无播放数据，不预过滤
        if (pubDate == DateTime.MinValue) return true; // 无发布时间，不预过滤

        var days = (now - pubDate).TotalDays;

        // 发布不足 1 天：条件1 需要 days > 1，必然被过滤，直接跳过
        if (days < 1) return false;
        // 条件1（1~7 天）无条件通过，全部请求详情
        if (days < 7) return true;
        // 条件2：7~30 天需播放 > 3000，预留 20% 余量
        if (days < 30) return playFromSearch > 2400;
        // 条件3：30~180 天需播放 > 50000，预留 20% 余量
        if (days < 180) return playFromSearch > 40000;
        // 条件4：≥180 天需播放 > 100000，预留 20% 余量
        return playFromSearch > 80000;
    }
}
