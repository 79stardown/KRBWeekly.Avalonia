using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KRBWeekly.Helpers;
using KRBWeekly.Models;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

public partial class StatisticsView : UserControl
{
    private const int CLICK_MAX_PAGES = 50;        // click/pubdate 排序页上限（API 单排序最多 1000 条）
    private const int DEFAULT_LOOKBACK_DAYS = 190; // 规则集关闭/无时间条件时的默认回看天数
    private const int MAX_LOOKBACK_DAYS = 3650;    // 回看上限，防止规则里填出天文数字后空翻页

    private readonly string _dataDir;
    private readonly BilibiliApiService _api;
    private readonly ListService _listSvc;
    private readonly ConfigService _configSvc;
    private ScoringConfig _config = new();
    private List<VideoInfo> _results = new();
    private bool _isRunning;
    private bool _rateLimitNotified;
    private DateTime _effectiveNow;

    /// <summary>统计完成并自动存档后触发（MainWindow 订阅后自动切换到筛选视图）</summary>
    public event Action? StatisticsCompleted;

    public StatisticsView()
    {
        InitializeComponent();

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppContext.BaseDirectory;
        _dataDir = Path.Combine(exeDir, "Data");
        Directory.CreateDirectory(_dataDir);
        LogService.Init(_dataDir);

        _api = new BilibiliApiService();
        _listSvc = new ListService(_dataDir);
        _configSvc = new ConfigService(_dataDir);
        Loaded += (_, _) =>
        {
            LoadListStatus();
            LoadSearchSettings();
            DpSpecific.SelectedDate = DateTime.Today;
        };
    }

    /// <summary>每次进入本页都重读 settings.json：标签与规则可能在设置页被改过</summary>
    private void LoadSearchSettings()
    {
        var c = _configSvc.Load();
        TxtSearchTag.Text = c.SearchKeyword;

        var rules = c.FilterRules ?? FilterRuleSet.CreateDefault();
        TxtRuleStatus.Text = $"筛选规则: {rules.Summary()}　（在「设置 → 计算与统计」中编辑）";
    }

    /// <summary>规整搜索标签：去首尾空白、去掉用户自带的外层引号、压缩连续空格</summary>
    private static string NormalizeTag(string? raw)
    {
        var t = (raw ?? "").Trim();
        while (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
            t = t[1..^1].Trim();
        return Regex.Replace(t, @"\s+", " ");
    }

    private void LoadListStatus()
    {
        var white = _listSvc.LoadWhitelist();
        var black = _listSvc.LoadBlacklist();
        TxtListStatus.Text = $"白名单: {white.Count} 个  |  黑名单: {black.Count} 个";
    }

    private void DateMode_Changed(object? sender, RoutedEventArgs e)
    {
        DpSpecific.IsEnabled = RbSpecific.IsChecked == true;
    }

    private async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        // 搜索标签先规整再落盘：空标签会把全网视频都搜进来，必须拦住
        var cfg = _configSvc.Load();
        var tag = NormalizeTag(TxtSearchTag.Text);
        if (tag.Length == 0)
        {
            await UiDialog.WarnAsync("提示", "搜索标签不能为空。");
            TxtSearchTag.Focus();
            return;
        }
        TxtSearchTag.Text = tag;
        if (!string.Equals(tag, cfg.SearchKeyword, StringComparison.Ordinal))
        {
            cfg.SearchKeyword = tag;
            _configSvc.Save(cfg);
        }

        // 旧版单文件迁移要放在「首次统计」判断之前，否则老用户会被误判成首次
        var migrationNotes = new List<string>();
        DataScope.MigrateLegacy(_dataDir, tag, m => migrationNotes.Add(m));

        // 1) 上次已经被 412 拦过：优先提醒，别急着再打
        var state = RunStateService.Load(_dataDir);
        var rateRemain = RunGuard.RateLimitRemainingMinutes(state.LastRateLimitUtc, DateTime.UtcNow);
        var rateWarned = false;
        if (rateRemain > 0)
        {
            var go = await UiDialog.ConfirmAsync("提示",
                $"上次统计触发了 B 站风控（HTTP 412），建议再等 {rateRemain} 分钟。\n\n" +
                "风控期间连续重试会让封锁时间变长；换网络（如手机热点）通常能立刻恢复。\n\n现在仍要统计吗？",
                "仍要统计", "稍后再试");
            if (!go) return;
            rateWarned = true;
        }

        // 2) 短时间内重复统计：先提醒 412 风控（仍可强行继续）
        var remain = RunGuard.CooldownRemainingMinutes(state.LastRunUtc, DateTime.UtcNow);
        if (remain > 0 && !rateWarned)
        {
            var lastTime = state.LastRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
            var lastTag = string.IsNullOrWhiteSpace(state.LastTag) ? "" : $"（标签「{state.LastTag}」）";
            var go = await UiDialog.ConfirmAsync("提示",
                $"短时间内重复统计会触发 B 站 412 风控，建议 {remain} 分钟后再试。\n\n" +
                $"上次统计：{lastTime}{lastTag}\n\n现在仍要统计吗？",
                "仍要统计", "稍后再试");
            if (!go) return;
        }

        // 3) 该标签第一次统计：拿到的是累计值，不是周增量
        if (RunGuard.IsFirstRunForTag(_dataDir, tag))
        {
            var go = await UiDialog.ConfirmAsync("提示",
                $"「{tag}」标签为首次统计：本次统计结果是视频的全部累计数据，而不是本周所需增量。\n\n" +
                "请在下一个统计周期再次统计，才能得到正确的周增量数值。",
                "继续统计", "取消");
            if (!go) return;
        }

        // 记录本次统计时间，供下次重复统计时提醒
        RunStateService.Save(_dataDir, tag);

        // 开始新的日志会话（写入 Data/Logs/）
        LogService.StartSession();
        foreach (var note in migrationNotes) Log(note);

        _isRunning = true;
        BtnStart.IsEnabled = false;
        TxtLog.Text = "";

        try
        {
            _effectiveNow = RbToday.IsChecked == true
                ? DateTime.Now
                : DpSpecific.SelectedDate?.Date.AddHours(12) ?? DateTime.Now;

            await RunStatisticsAsync();
        }
        catch (Exception ex)
        {
            Log($"\n[!] 程序出错: {ex.Message}");
        }
        finally
        {
            _isRunning = false;
            BtnStart.IsEnabled = true;
            ProgressBar.Value = 0;
            TxtProgress.Text = "";
        }
    }

    private async Task RunStatisticsAsync()
    {
        var cfg = _configSvc.Load();
        var rules = cfg.FilterRules ?? FilterRuleSet.CreateDefault();
        var tag = NormalizeTag(cfg.SearchKeyword);
        _rateLimitNotified = false;

        Log("==================================================");
        Log("KARDS金曲 周榜统计工具");
        Log($"统计日期: {_effectiveNow:yyyy-MM-dd}");
        Log($"搜索标签: 「{tag}」");
        Log($"筛选规则: {rules.Summary()}");
        // 现取现读：本方法内的 _config 要到逐视频循环里才 Load，
        // 用字段会打出上一轮的（或默认的）模式，日志与实际算法不符
        Log($"计算方式: {(string.Equals(cfg.CalcMode, "Simple", StringComparison.OrdinalIgnoreCase) ? "单纯相加" : "修正系数")}");
        Log("==================================================");

        // 起算值按标签隔离：读写属于本标签的那一份（旧文件迁移已在开始按钮里做过）
        var historySvc = new HistoryService(_dataDir, tag);

        // 获取只读快照（不修改实际 history）
        var snapshot = historySvc.GetSnapshot();
        Log($"history 快照: {snapshot.Count} 个视频基线（标签「{tag}」）");
        var whitelist = _listSvc.LoadWhitelist();
        var blacklist = _listSvc.LoadBlacklist();
        Log($"白名单: {whitelist.Count} 个, 黑名单: {blacklist.Count} 个");

        // 规则集要求回看多久 → 决定 pubdate 轮的停止阈值。
        // （旧版写死 190 天是按「条件4 = 180 天」推的；规则可编辑后必须跟着规则走）
        double? lookback = rules.HasRules ? rules.RequiredLookbackDays() : DEFAULT_LOOKBACK_DAYS;
        int? stopDays = lookback is null
            ? null
            : (int)Math.Clamp(Math.Ceiling(lookback.Value) + 10, 1, MAX_LOOKBACK_DAYS);
        if (stopDays is null)
            Log("[!] 规则集没有发布时间下限，轮2 不做自适应停止（最多翻 50 页 / 1000 条）");

        // 搜索（两轮：精确短语按点击排序覆盖高播放视频 + 分词按发布时间排序兜住近期新视频与标题变体）
        var rounds = new List<SearchRound>
        {
            new()
            {
                Keyword = $"\"{tag}\"",  // 精确短语
                Order = "click",
                MaxPages = CLICK_MAX_PAGES,
                Label = $"轮1 精确短语「{tag}」 order=click",
            },
            new()
            {
                Keyword = tag,  // 不带引号，分词匹配带空格/大小写等标题变体
                Order = "pubdate",
                MaxPages = CLICK_MAX_PAGES,
                PubdateStopBefore = stopDays is null ? null : _effectiveNow.AddDays(-stopDays.Value),
                Label = stopDays is null
                    ? $"轮2 分词「{tag}」 order=pubdate(不限回看)"
                    : $"轮2 分词「{tag}」 order=pubdate(自适应{stopDays}天)",
            },
        };
        var videos = await _api.SearchVideosAsync(rounds, msg => Log(msg));

        // 搜索阶段就被风控拦了：先明确告诉用户，别让它看起来像「本周没视频」
        if (_api.RateLimited)
            await NotifyRateLimitedAsync("搜索阶段被拦截，本次结果可能不完整");

        // 搜索一个视频都没拿到 = 这轮结果不可信（实测最常见：B 站风控，全部页 HTTP 412）。
        // 必须在此之前中止：下面的白名单补录会把列表撑成「只有补录视频」，
        // 于是「已自动写入起算值 + 自动存档」照常执行，一次失败运行就覆盖掉本周榜单。
        if (videos.Count == 0)
        {
            Log("[!] 搜索未获取到任何视频，本次不写入起算值、不存档");
            Log("[!] 常见原因: B 站风控(HTTP 412) / 网络异常。请稍后重试，上方各轮日志有 HTTP 状态码");
            TxtStatus.Text = _api.RateLimited
                ? "已被 B 站风控拦截（412），本次中止，起算值未改动"
                : "搜索未获取到任何视频，已中止（起算值未改动）";
            return;
        }

        // 补充白名单
        var existingBvids = new HashSet<string>(videos.Select(v => v.Bvid));
        foreach (var bvid in whitelist)
        {
            if (!existingBvids.Contains(bvid))
            {
                Log($"补充白名单视频: {bvid}");
                videos.Add(new VideoInfo { Bvid = bvid, Title = "白名单视频" });
            }
        }

        if (videos.Count == 0)
        {
            Log("[!] 没有找到任何视频");
            return;
        }

        // 详情预过滤：用搜索结果自带的播放量做宽松初筛，减少详情请求量
        var toFetch = new List<VideoInfo>();
        foreach (var v in videos)
        {
            var isW = whitelist.Contains(v.Bvid);
            var isB = blacklist.Contains(v.Bvid);
            if (VideoFilterService.CouldPassAtSearch(v.PubDate, v.PlayFromSearch, isW, isB, _effectiveNow, rules))
                toFetch.Add(v);
        }
        Log($"预过滤: {videos.Count} → {toFetch.Count} 个视频需要请求详情 (跳过 {videos.Count - toFetch.Count} 个不达标视频)");
        _api.AppendPrefilterToSummary(toFetch.Count);
        videos = toFetch;

        Log($"共 {videos.Count} 个视频待处理");
        Log("--------------------------------------------------");

        _results = new List<VideoInfo>();
        var failedBvids = new List<string>();
        int rejectedByRules = 0;
        int processed = 0;
        SetProgress(0, videos.Count);

        foreach (var v in videos)
        {
            processed++;
            var bvid = v.Bvid;
            var title = v.Title;
            var isWhitelisted = whitelist.Contains(bvid);
            var isBlacklisted = blacklist.Contains(bvid);

            Log($"[{processed}/{videos.Count}] 正在获取 {bvid} ...");
            var info = await _api.GetVideoInfoAsync(bvid, 2, msg => Log(msg));
            if (info == null)
            {
                Log($"[!] 获取视频 {bvid} 信息失败（已重试）");
                failedBvids.Add(bvid);
                continue;
            }
            info.Title = title.Length > 0 ? title : info.Title;
            info.IsWhitelist = isWhitelisted;

            var (qualified, daysDiff, reason) = VideoFilterService.CheckEligible(
                info.PubDate, info.View, info.Favorite, info.Like, info.Coin,
                isWhitelisted, isBlacklisted, _effectiveNow, rules);

            info.DaysDiff = daysDiff;
            info.FilterReason = reason;

            if (!qualified)
            {
                if (!isBlacklisted) rejectedByRules++;
                Log(isBlacklisted
                    ? $"[!] 黑名单跳过: {info.Title.Truncate(30)}"
                    : $"跳过 ({reason}): {info.Title.Truncate(30)}");
                continue;
            }

            Log(isWhitelisted
                ? $"[OK] 白名单通过: {info.Title.Truncate(30)}"
                : $"[OK] {reason}: {info.Title.Truncate(30)}");

            // 基于快照计算增量（不修改 history，纯计算）
            var (delta, isNew, lastRank, deltaFactor) = HistoryService.ComputeDelta(
                snapshot, bvid, info.View, info.Like, info.Coin, info.Favorite, info.DaysDiff);
            info.DeltaFactor = deltaFactor;
            if (isNew && deltaFactor < 0.999)
                Log($"   新上榜折算: 发布{daysDiff:F0}天, 系数 x{deltaFactor:F3}");

            info.DeltaView = delta["view"];
            info.DeltaLike = delta["like"];
            info.DeltaCoin = delta["coin"];
            info.DeltaFavorite = delta["favorite"];
            info.IsNew = isNew;
            info.LastRank = lastRank;
            info.Status = isNew
                ? $"新上榜{(isWhitelisted ? " (白名单)" : "")}"
                : $"已存在{(isWhitelisted ? " (白名单)" : "")}";

            _config = _configSvc.Load();
            var score = ScoreCalculator.CalcScore(
                info.DeltaView, info.DeltaFavorite, info.DeltaLike, info.DeltaCoin, daysDiff, _config);
            info.TotalScore = score.Total;
            info.PlayScore = score.PlayScore;
            info.FavScore = score.FavScore;
            info.LikeCoinScore = score.LikeCoinScore;
            info.CoefA = score.CoefA;
            info.CoefB = score.CoefB;
            info.CoefC = score.CoefC;

            _results.Add(info);

            Log($"   新增: 播放+{info.DeltaView} 收藏+{info.DeltaFavorite} " +
                $"点赞+{info.DeltaLike} 投币+{info.DeltaCoin}");
            Log($"   诊断: 当前总量={info.View} | 新增={info.DeltaView} | " +
                $"{daysDiff:F0}天前发布");
            Log($"   总分: {info.TotalScore:F2} (播放分:{info.PlayScore:F1} " +
                $"收藏分:{info.FavScore:F1} 赞币分:{info.LikeCoinScore:F1})");

            SetProgress(processed, videos.Count);
            await Task.Delay(300);
        }

        SetProgress(videos.Count, videos.Count);

        // 逐个取详情时才被拦的（搜索阶段没触发）：同样提示一次
        if (_api.RateLimited && !_rateLimitNotified)
            await NotifyRateLimitedAsync("逐个取详情时被拦截，可能有视频漏计");

        if (_results.Count == 0)
        {
            Log("[!] 没有合格视频，未写入起算值");
            return;
        }

        // 排序
        var sorted = _results.OrderByDescending(r => r.TotalScore).ToList();

        // 计算排名变化（写入 history 的 last_rank 依据）
        for (int i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            r.Rank = i + 1;
            if (r.LastRank == null) { r.RankChange = "新上榜"; r.RankChangeShort = "新"; }
            else if (r.LastRank < r.Rank) { var d = r.Rank - r.LastRank.Value; r.RankChange = $"↓↓{d}"; r.RankChangeShort = $"↓{d}"; }
            else if (r.LastRank > r.Rank) { var d = r.LastRank.Value - r.Rank; r.RankChange = $"↑↑{d}"; r.RankChangeShort = $"↑{d}"; }
            else { r.RankChange = "→ 持平"; r.RankChangeShort = "→"; }
        }

        Log($"\n共统计 {_results.Count} 个视频");
        if (rejectedByRules > 0)
            Log($"规则集筛除 {rejectedByRules} 个不满足条件的视频（白名单不受限，黑名单不计数）");

        // 失败视频统一汇总，避免静默漏榜
        if (failedBvids.Count > 0)
        {
            Log("");
            Log($"[!] 以下 {failedBvids.Count} 个视频获取失败，可能未计入榜单，请核对" +
                $"(可将确认属实的 BV 加入白名单后重跑):");
            Log($"   {string.Join("  ", failedBvids)}");
            Log("");
        }

        // ===== 全自动写入起算值（用户已确认：无需勾选与确认） =====
        historySvc.LoadHistory();
        foreach (var r in sorted)
            historySvc.SetEntry(r.Bvid, r.View, r.Like, r.Coin, r.Favorite);
        historySvc.UpdateRanks(sorted.Select(r => r.Bvid).ToList());
        historySvc.SaveHistory();
        Log($"已自动写入起算值: {sorted.Count} 个视频 → History/{Path.GetFileName(historySvc.FilePath)}（旧版已备份 .backup）");

        // 自动存档记录（筛选页数据来源）
        SaveRecord(sorted, tag);

        // 本次没被风控：清掉旧标记，避免之后 30 分钟里一直提示
        if (!_api.RateLimited)
            RunStateService.ClearRateLimit(_dataDir);

        TxtStatus.Text = failedBvids.Count > 0
            ? $"统计完成（标签「{tag}」），共 {_results.Count} 个视频，起算值已自动保存（[!] {failedBvids.Count} 个获取失败，见日志）"
            : $"统计完成（标签「{tag}」），共 {_results.Count} 个视频，起算值已自动保存";

        StatisticsCompleted?.Invoke();
    }

    /// <summary>把本次结果写入 Data/Records/&lt;标签&gt;/记录_*.json（供筛选页选择）</summary>
    private void SaveRecord(List<VideoInfo> sorted, string tag)
    {
        try
        {
            var now = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var recordsDir = DataScope.RecordsDirectory(_dataDir, tag);
            Directory.CreateDirectory(recordsDir);
            var recordPath = Path.Combine(recordsDir, $"记录_{now}.json");
            var json = JsonSerializer.Serialize(sorted, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(recordPath, json, Encoding.UTF8);
            Log($"结果已自动记录: Records/{DataScope.FolderName(tag)}/记录_{now}.json");
        }
        catch (Exception ex)
        {
            Log($"[!] 存档记录失败: {ex.Message}");
        }
    }

    /// <summary>撞上 412 后的统一处理：记一笔状态（下次启动前会提醒）+ 弹窗说明怎么处理</summary>
    private async Task NotifyRateLimitedAsync(string detail)
    {
        _rateLimitNotified = true;
        RunStateService.MarkRateLimited(_dataDir);

        Log($"[!] 触发 B 站风控(-412)：{detail}");
        TxtStatus.Text = "已被 B 站风控拦截（412），建议稍后再试";

        await UiDialog.WarnAsync("已被 B 站风控拦截",
            $"本次统计触发了 B 站风控（HTTP 412）：{detail}\n\n" +
            "建议等待 10~30 分钟后再试，期间不要连续重试——越重试封锁时间越长。\n" +
            "换网络（比如切到手机热点）通常能立刻恢复。");
    }

    private void SetProgress(int current, int total)    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (total > 0)
            {
                ProgressBar.Maximum = total;
                ProgressBar.Value = current;
                TxtProgress.Text = $"{current} / {total}";
            }
        });
    }

    private void Log(string msg)
    {
        // 同步写入日志文件（供「设置 → 数据文件 → 运行日志」查看）
        LogService.Append(msg);

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            TxtLog.Text += msg + Environment.NewLine;
            TxtLog.CaretIndex = TxtLog.Text.Length;
        });
    }
}
