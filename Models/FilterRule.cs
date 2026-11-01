using System.Linq;

namespace KRBWeekly.Models;

/// <summary>
/// 规则可用的字段。持久化用字符串码（不随枚举顺序变化），
/// 这样以后加减字段不会把用户已存的 settings.json 解释歪。
/// </summary>
public static class FilterField
{
    public const string Days = "Days";         // 发布至今的天数
    public const string View = "View";         // 播放量
    public const string Favorite = "Favorite"; // 收藏量
    public const string Like = "Like";         // 点赞量
    public const string Coin = "Coin";         // 投币量

    public static readonly (string Code, string Label)[] All =
    {
        (Days, "发布时间（天）"),
        (View, "播放量"),
        (Favorite, "收藏量"),
        (Like, "点赞量"),
        (Coin, "投币量"),
    };

    public static int IndexOf(string? code)
    {
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    public static string CodeAt(int index)
        => index >= 0 && index < All.Length ? All[index].Code : Days;

    public static string LabelOf(string? code) => All[IndexOf(code)].Label;
}

/// <summary>比较方式。单值比较统一把阈值放在 Min 上，区间用 Min~Max。</summary>
public static class FilterOp
{
    public const string Gt = "Gt";
    public const string Ge = "Ge";
    public const string Lt = "Lt";
    public const string Le = "Le";
    public const string Between = "Between";
    public const string Eq = "Eq";

    public static readonly (string Code, string Label)[] All =
    {
        (Gt, "大于"),
        (Ge, "大于等于"),
        (Lt, "小于"),
        (Le, "小于等于"),
        (Between, "区间"),
        (Eq, "等于"),
    };

    public const int IndexBetween = 4;

    public static int IndexOf(string? code)
    {
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
        return 1; // 默认「大于等于」
    }

    public static string CodeAt(int index)
        => index >= 0 && index < All.Length ? All[index].Code : Ge;

    public static string LabelOf(string? code) => All[IndexOf(code)].Label;
}

/// <summary>条件之间的组合方式（组间、组内都用它）</summary>
public static class FilterMatch
{
    public const string Any = "Any"; // 任一满足（OR）
    public const string All = "All"; // 全部满足（AND）
}

/// <summary>单条筛选条件</summary>
public class FilterRule
{
    /// <summary>字段码，见 <see cref="FilterField"/></summary>
    public string Field { get; set; } = FilterField.Days;

    /// <summary>比较方式码，见 <see cref="FilterOp"/></summary>
    public string Op { get; set; } = FilterOp.Ge;

    /// <summary>单值比较的阈值；区间比较的下界</summary>
    public double Min { get; set; }

    /// <summary>区间比较的上界（仅 Op = Between 时有意义）</summary>
    public double Max { get; set; }

    /// <summary>取反（「非」）：条件成立时反而判为不满足</summary>
    public bool IsReversed { get; set; }

    /// <summary>取出本条件对应的实际数值</summary>
    public double ValueOf(double days, double view, double favorite, double like, double coin) => Field switch
    {
        FilterField.View => view,
        FilterField.Favorite => favorite,
        FilterField.Like => like,
        FilterField.Coin => coin,
        _ => days,
    };

    public bool Test(double days, long view, long favorite, long like, long coin)
    {
        var v = ValueOf(days, view, favorite, like, coin);
        var lo = Math.Min(Min, Max);
        var hi = Math.Max(Min, Max);

        var ok = Op switch
        {
            FilterOp.Gt => v > Min,
            FilterOp.Ge => v >= Min,
            FilterOp.Lt => v < Min,
            FilterOp.Le => v <= Min,
            FilterOp.Eq => Math.Abs(v - Min) < 0.5,
            FilterOp.Between => v >= lo && v <= hi,
            _ => true,
        };

        return IsReversed ? !ok : ok;
    }

    /// <summary>给日志/提示用的一行描述，如「发布时间（天） ≥ 7」</summary>
    public string Describe()
    {
        var label = FilterField.LabelOf(Field);
        var body = Op == FilterOp.Between
            ? $"{label} {Math.Min(Min, Max):0.##}~{Math.Max(Min, Max):0.##}"
            : $"{label} {FilterOp.LabelOf(Op)} {Min:0.##}";
        return IsReversed ? $"非（{body}）" : body;
    }

    public FilterRule Clone() => new()
    {
        Field = Field,
        Op = Op,
        Min = Min,
        Max = Max,
        IsReversed = IsReversed,
    };
}

/// <summary>一组条件：组内按 <see cref="MatchMode"/> 组合，整组即一个「规则组」</summary>
public class FilterRuleGroup
{
    public bool Enabled { get; set; } = true;

    /// <summary>组内关系：Any = 任一条件满足；All = 全部条件满足</summary>
    public string MatchMode { get; set; } = FilterMatch.All;

    public List<FilterRule> Rules { get; set; } = new();

    public bool Matches(double days, long view, long favorite, long like, long coin)
    {
        if (Rules.Count == 0) return false;

        return string.Equals(MatchMode, FilterMatch.Any, StringComparison.OrdinalIgnoreCase)
            ? Rules.Any(r => r.Test(days, view, favorite, like, coin))
            : Rules.All(r => r.Test(days, view, favorite, like, coin));
    }

    public FilterRuleGroup Clone() => new()
    {
        Enabled = Enabled,
        MatchMode = MatchMode,
        Rules = Rules.Select(r => r.Clone()).ToList(),
    };
}

/// <summary>
/// 自动筛选规则集。
///
/// 语义与 ClassIsland 的规则集一致，三层结构：
///   条件（可「非」）→ 规则组（组内 任一/全部，可启用）→ 规则集（组间 任一/全部，可启用）
///
/// 默认预设 4 组，与原 A/B/C/D 四级过滤表完全等价：
///   G1 1 &lt; 天数 &lt; 7
///   G2 7 ≤ 天数 &lt; 30    且 播放 &gt; 3000
///   G3 30 ≤ 天数 &lt; 180  且 播放 &gt; 50000
///   G4 天数 ≥ 180        且 播放 &gt; 100000
/// 关闭 Enabled 或没有任何有效条件时，等于不筛选（只受黑白名单约束）。
/// </summary>
public class FilterRuleSet
{
    public bool Enabled { get; set; } = true;

    /// <summary>组间关系：Any = 任一满足；All = 全部满足</summary>
    public string MatchMode { get; set; } = FilterMatch.Any;

    public List<FilterRuleGroup> Groups { get; set; } = new();

    /// <summary>是否真的会筛掉东西（关闭/空集时一律放行）</summary>
    public bool HasRules => Enabled && Groups.Any(g => g.Enabled && g.Rules.Count > 0);

    public IEnumerable<FilterRuleGroup> ActiveGroups => Groups.Where(g => g.Enabled && g.Rules.Count > 0);

    /// <summary>按规则集判定是否合格</summary>
    public (bool Pass, string Reason) Evaluate(
        double days, long view, long favorite, long like, long coin)
    {
        var groups = ActiveGroups.ToList();
        if (!Enabled || groups.Count == 0)
            return (true, "规则集未启用");

        if (string.Equals(MatchMode, FilterMatch.All, StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < groups.Count; i++)
                if (!groups[i].Matches(days, view, favorite, like, coin))
                    return (false, $"不满足规则集（卡在规则组{i + 1}）");
            return (true, "全部规则组通过");
        }

        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Matches(days, view, favorite, like, coin))
                return (true, $"规则组{i + 1}通过");

        return (false, "不满足规则集");
    }

    /// <summary>一行摘要，给统计页/日志显示</summary>
    public string Summary()
    {
        if (!Enabled) return "已关闭（不筛选）";
        var groups = ActiveGroups.ToList();
        if (groups.Count == 0) return "空规则集（不筛选）";
        var mode = string.Equals(MatchMode, FilterMatch.All, StringComparison.OrdinalIgnoreCase) ? "全部满足" : "任一满足";
        var rules = groups.Sum(g => g.Rules.Count);
        return $"已启用 · {groups.Count} 组 / {rules} 条条件 · 组间{mode}";
    }

    /// <summary>
    /// 搜索阶段需要回看多少天（用于 pubdate 轮的自适应停止）。
    /// 返回 null 表示「不限发布时间」——即存在一组完全不看天数的规则组（Any 模式），
    /// 此时不能提前停止翻页。
    /// </summary>
    public double? RequiredLookbackDays()
    {
        if (!HasRules) return null;

        var groups = ActiveGroups.ToList();
        var isAny = !string.Equals(MatchMode, FilterMatch.All, StringComparison.OrdinalIgnoreCase);
        var anyDayRule = false;
        double maxDays = 0;

        foreach (var g in groups)
        {
            var dayRules = g.Rules.Where(r => r.Field == FilterField.Days).ToList();
            if (dayRules.Count == 0)
            {
                if (isAny) return null; // 该组不限年龄，任何老视频都可能靠它进榜
                continue;
            }

            anyDayRule = true;
            foreach (var r in dayRules)
            {
                var bound = r.Op == FilterOp.Between ? Math.Max(r.Min, r.Max) : r.Min;
                if (bound > maxDays) maxDays = bound;
            }
        }

        return anyDayRule ? maxDays : null;
    }

    /// <summary>深拷贝（编辑器里取消/重置时不污染正在使用的对象）</summary>
    public FilterRuleSet Clone() => new()
    {
        Enabled = Enabled,
        MatchMode = MatchMode,
        Groups = Groups.Select(g => g.Clone()).ToList(),
    };

    /// <summary>内置预设：等价于旧版 A/B/C/D 四级过滤表</summary>
    public static FilterRuleSet CreateDefault()
    {
        static FilterRule Rule(string field, string op, double min, double max = 0)
            => new() { Field = field, Op = op, Min = min, Max = max };

        return new FilterRuleSet
        {
            Enabled = true,
            MatchMode = FilterMatch.Any,
            Groups = new List<FilterRuleGroup>
            {
                // 条件1：1 < 天数 < 7，不限播放
                new()
                {
                    Rules =
                    {
                        Rule(FilterField.Days, FilterOp.Gt, 1),
                        Rule(FilterField.Days, FilterOp.Lt, 7),
                    }
                },
                // 条件2：7 ≤ 天数 < 30，播放 > 3000
                new()
                {
                    Rules =
                    {
                        Rule(FilterField.Days, FilterOp.Ge, 7),
                        Rule(FilterField.Days, FilterOp.Lt, 30),
                        Rule(FilterField.View, FilterOp.Gt, 3000),
                    }
                },
                // 条件3：30 ≤ 天数 < 180，播放 > 50000
                new()
                {
                    Rules =
                    {
                        Rule(FilterField.Days, FilterOp.Ge, 30),
                        Rule(FilterField.Days, FilterOp.Lt, 180),
                        Rule(FilterField.View, FilterOp.Gt, 50000),
                    }
                },
                // 条件4：天数 ≥ 180，播放 > 100000
                new()
                {
                    Rules =
                    {
                        Rule(FilterField.Days, FilterOp.Ge, 180),
                        Rule(FilterField.View, FilterOp.Gt, 100000),
                    }
                },
            }
        };
    }
}
