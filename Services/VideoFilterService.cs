using KRBWeekly.Models;

namespace KRBWeekly.Services;

/// <summary>
/// 视频过滤服务 —— 由「规则集」驱动（默认预设等价于旧版 A/B/C/D 四级过滤表）。
///
/// 判定顺序：
///   1. 黑名单 → 直接拒绝（白名单也不救）
///   2. 白名单 → 直接通过
///   3. 规则集关闭 / 没有有效条件 → 全部通过
///   4. 否则按 <see cref="FilterRuleSet.Evaluate"/> 判定：组内 AND，组间 OR/AND
/// </summary>
public static class VideoFilterService
{
    public static (bool Qualified, double DaysDiff, string Reason) CheckEligible(
        DateTime pubDate, long view, long favorite, long like, long coin,
        bool isWhitelisted, bool isBlacklisted,
        DateTime now, FilterRuleSet? rules)
    {
        var daysDiff = (now - pubDate).TotalDays;

        if (isBlacklisted)
            return (false, daysDiff, "黑名单");

        if (isWhitelisted)
            return (true, daysDiff, "白名单");

        if (rules == null || !rules.HasRules)
            return (true, daysDiff, "规则集未启用");

        var (pass, reason) = rules.Evaluate(daysDiff, view, favorite, like, coin);
        return (pass, daysDiff, $"{reason} (发布{daysDiff:F1}天, 播放{view})");
    }

    /// <summary>
    /// 详情预过滤 —— 用搜索结果自带的播放量做宽松初筛，减少逐视频详情请求量。
    ///
    /// 原则：只跳过「按搜索结果已经能断定必然过不了规则集」的视频。
    ///   · 发布天数在搜索结果里是精确值，按精确值判定；
    ///   · 播放量给 20% 余量（搜索卡片数字与详情口径不完全一致）；
    ///   · 收藏/点赞/投币在搜索阶段拿不到，一律视为「有可能满足」，放行去取详情；
    ///   · 黑名单直接跳过，白名单强制保留。
    /// 规则集关闭或没有有效条件时全部放行。
    /// </summary>
    public static bool CouldPassAtSearch(
        DateTime pubDate, long playFromSearch,
        bool isWhitelisted, bool isBlacklisted,
        DateTime now, FilterRuleSet? rules)
    {
        if (isBlacklisted) return false;               // 黑名单无需详情数据
        if (isWhitelisted) return true;                // 白名单强制保留
        if (rules == null || !rules.HasRules) return true;
        if (pubDate == DateTime.MinValue) return true; // 无发布时间，不预过滤
        if (playFromSearch < 0) return true;           // 无播放数据，不预过滤

        var days = (now - pubDate).TotalDays;
        var groups = rules.ActiveGroups.ToList();
        var isAll = string.Equals(rules.MatchMode, FilterMatch.All, StringComparison.OrdinalIgnoreCase);

        bool anyPossible = false;
        foreach (var g in groups)
        {
            var possible = GroupCouldPass(g, days, playFromSearch);
            if (!possible)
            {
                if (isAll) return false; // 全部满足模式下只要有一组必败就直接跳过
                continue;
            }
            anyPossible = true;
            if (!isAll) return true;     // 任一满足模式下有一组可能通过就够了
        }

        return anyPossible;
    }

    /// <summary>整组在搜索阶段是否还有通过的可能（按组内 任一/全部 组合）</summary>
    private static bool GroupCouldPass(FilterRuleGroup g, double days, long playFromSearch)
    {
        if (g.Rules.Count == 0) return false;

        return string.Equals(g.MatchMode, FilterMatch.Any, StringComparison.OrdinalIgnoreCase)
            ? g.Rules.Any(r => RuleCouldPass(r, days, playFromSearch))
            : g.Rules.All(r => RuleCouldPass(r, days, playFromSearch));
    }

    /// <summary>单条条件在「只知道天数和搜索播放量」时是否可能成立</summary>
    private static bool RuleCouldPass(FilterRule rule, double days, long playFromSearch)
    {
        // 取了反的条件无法预判，一律放行去取详情
        if (rule.IsReversed) return true;

        // 收藏/点赞/投币在搜索阶段未知 → 不能否定
        double? value = rule.Field switch
        {
            FilterField.Days => days,
            FilterField.View => playFromSearch,
            _ => null,
        };
        if (value is null) return true;

        var v = value.Value;
        // 播放量口径差异：下界打 8 折、上界放大 25%，只排除明显不达标的
        var slackLow = rule.Field == FilterField.View ? 0.8 : 1.0;
        var slackHigh = rule.Field == FilterField.View ? 1.25 : 1.0;

        var lo = Math.Min(rule.Min, rule.Max);
        var hi = Math.Max(rule.Min, rule.Max);

        return rule.Op switch
        {
            FilterOp.Gt or FilterOp.Ge => v > rule.Min * slackLow - 0.001,
            FilterOp.Lt or FilterOp.Le => v <= rule.Min * slackHigh + 0.001,
            FilterOp.Eq => Math.Abs(v - rule.Min) <= Math.Max(rule.Min * 0.25, 1),
            FilterOp.Between => v >= lo * slackLow - 0.001 && v <= hi * slackHigh + 0.001,
            _ => true,
        };
    }
}
