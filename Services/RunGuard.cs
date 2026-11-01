using System.IO;
using System.Text.Json;

namespace KRBWeekly.Services;

/// <summary>上一次统计的运行时状态（Data\run_state.json，不属于用户配置）</summary>
public class RunState
{
    public DateTime? LastRunUtc { get; set; }
    public string LastTag { get; set; } = "";

    /// <summary>最近一次撞上 -412 风控的时间（UTC）</summary>
    public DateTime? LastRateLimitUtc { get; set; }
}

/// <summary>读写「上次统计时间」，用于判断是否在短时间内重复统计</summary>
public static class RunStateService
{
    private static string PathFor(string dataDir) => Path.Combine(dataDir, "run_state.json");

    public static RunState Load(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            if (!File.Exists(path)) return new RunState();

            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<RunState>(json) ?? new RunState();
        }
        catch
        {
            return new RunState();
        }
    }

    public static void Save(string dataDir, string tag)
    {
        // 读-改-写：别把上一次的风控记录冲掉
        var state = Load(dataDir);
        state.LastRunUtc = DateTime.UtcNow;
        state.LastTag = tag ?? "";
        Write(dataDir, state);
    }

    /// <summary>记下「刚被 412 拦过」，下次统计前会先提醒</summary>
    public static void MarkRateLimited(string dataDir)
    {
        var state = Load(dataDir);
        state.LastRateLimitUtc = DateTime.UtcNow;
        Write(dataDir, state);
    }

    /// <summary>统计正常跑完：清掉风控标记，避免 30 分钟内一直提示</summary>
    public static void ClearRateLimit(string dataDir)
    {
        var state = Load(dataDir);
        if (state.LastRateLimitUtc is null) return;
        state.LastRateLimitUtc = null;
        Write(dataDir, state);
    }

    private static void Write(string dataDir, RunState state)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(state), System.Text.Encoding.UTF8);
        }
        catch
        {
            // 记不下来不影响统计本身
        }
    }

    /// <summary>测试/排障用：清掉记录</summary>
    public static void Clear(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}

/// <summary>
/// 统计前的两道确认：重复统计的 412 风险、以及某个标签的首次统计提醒。
/// 判定逻辑抽出来是为了能单测（弹窗本身在 StatisticsView 里）。
/// </summary>
public static class RunGuard
{
    /// <summary>重复统计的冷却窗口：这段时间内再统计会先提醒 412 风险</summary>
    public const int RepeatCooldownMinutes = 10;

    /// <summary>被 412 拦过之后的提醒窗口：这段时间内再统计会提示「先别急」</summary>
    public const int RateLimitCooldownMinutes = 30;

    /// <summary>距离下次「不提示」还差几分钟；0 = 已过冷却期或从没统计过</summary>
    public static int CooldownRemainingMinutes(DateTime? lastRunUtc, DateTime nowUtc)
        => RemainingMinutes(lastRunUtc, RepeatCooldownMinutes, nowUtc);

    /// <summary>风控提醒还剩几分钟；0 = 没有风控记录或已经过了提醒窗口</summary>
    public static int RateLimitRemainingMinutes(DateTime? lastRateLimitUtc, DateTime nowUtc)
        => RemainingMinutes(lastRateLimitUtc, RateLimitCooldownMinutes, nowUtc);

    private static int RemainingMinutes(DateTime? sinceUtc, int windowMinutes, DateTime nowUtc)
    {
        if (sinceUtc is null) return 0;

        var elapsed = nowUtc - sinceUtc.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero; // 时钟回拨按「刚发生」处理

        var remain = TimeSpan.FromMinutes(windowMinutes) - elapsed;
        return remain <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remain.TotalMinutes);
    }

    /// <summary>
    /// 该标签是否还没攒下起算值（没有 History/&lt;标签&gt;.json，或者里面一条也没有）。
    /// 首次统计拿到的是累计值，必须在下一个周期再统计一次才是真正的周增量。
    /// </summary>
    public static bool IsFirstRunForTag(string dataDir, string tag)
        => HistoryService.CountEntries(DataScope.HistoryFile(dataDir, tag)) <= 0;
}
