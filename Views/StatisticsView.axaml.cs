using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KRBWeekly.Helpers;
using KRBWeekly.Models;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

public partial class StatisticsView : UserControl
{
    private const string SEARCH_TAG = "\"KARDS金曲\"";  // 轮1: 精确短语
    private const int CLICK_MAX_PAGES = 50;             // click/pubdate 排序页上限（API 单排序最多 1000 条）
    private const int PUBDATE_STOP_DAYS = 190;          // 轮2 pubdate 自适应停止阈值（条件4 老视频已由轮1 覆盖）

    private readonly string _dataDir;
    private readonly BilibiliApiService _api;
    private readonly HistoryService _historySvc;
    private readonly ListService _listSvc;
    private readonly ConfigService _configSvc;
    private ScoringConfig _config = new();
    private List<VideoInfo> _results = new();
    private bool _isRunning;
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
        _historySvc = new HistoryService(_dataDir);
        _listSvc = new ListService(_dataDir);
        _configSvc = new ConfigService(_dataDir);
        Loaded += (_, _) =>
        {
            LoadListStatus();
            DpSpecific.SelectedDate = DateTime.Today;
        };
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

        // 开始新的日志会话（写入 Data/Logs/）
        LogService.StartSession();

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
        Log("==================================================");
        Log("KARDS金曲 周榜统计工具");
        Log($"统计日期: {_effectiveNow:yyyy-MM-dd}");
        Log("==================================================");

        // 获取只读快照（不修改实际 history）
        var snapshot = _historySvc.GetSnapshot();
        Log($"history 快照: {snapshot.Count} 个视频基线");
        var whitelist = _listSvc.LoadWhitelist();
        var blacklist = _listSvc.LoadBlacklist();
        Log($"白名单: {whitelist.Count} 个, 黑名单: {blacklist.Count} 个");

        // 搜索（两轮：精确短语按点击排序覆盖高播放视频 + 分词按发布时间排序兜住近期新视频与标题变体）
        var rounds = new List<SearchRound>
        {
            new()
            {
                Keyword = SEARCH_TAG,  // "KARDS金曲" 精确短语
                Order = "click",
                MaxPages = CLICK_MAX_PAGES,
                Label = $"轮1 精确短语{SEARCH_TAG} order=click",
            },
            new()
            {
                Keyword = "KARDS金曲",  // 不带引号，分词匹配带空格/大小写等标题变体
                Order = "pubdate",
                MaxPages = CLICK_MAX_PAGES,
                PubdateStopBefore = _effectiveNow.AddDays(-PUBDATE_STOP_DAYS),
                Label = $"轮2 分词 KARDS金曲 order=pubdate(自适应{PUBDATE_STOP_DAYS}天)",
            },
        };
        var videos = await _api.SearchVideosAsync(rounds, msg => Log(msg));

        // 搜索一个视频都没拿到 = 这轮结果不可信（实测最常见：B 站风控，全部页 HTTP 412）。
        // 必须在此之前中止：下面的白名单补录会把列表撑成「只有补录视频」，
        // 于是「已自动写入起算值 + 自动存档」照常执行，一次失败运行就覆盖掉本周榜单。
        if (videos.Count == 0)
        {
            Log("[!] 搜索未获取到任何视频，本次不写入起算值、不存档");
            Log("[!] 常见原因: B 站风控(HTTP 412) / 网络异常。请稍后重试，上方各轮日志有 HTTP 状态码");
            TxtStatus.Text = "搜索未获取到任何视频，已中止（起算值未改动）";
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
            if (VideoFilterService.ShouldFetchDetail(v.PubDate, v.PlayFromSearch, isW, isB, _effectiveNow))
                toFetch.Add(v);
        }
        Log($"预过滤: {videos.Count} → {toFetch.Count} 个视频需要请求详情 (跳过 {videos.Count - toFetch.Count} 个不达标视频)");
        _api.AppendPrefilterToSummary(toFetch.Count);
        videos = toFetch;

        Log($"共 {videos.Count} 个视频待处理");
        Log("--------------------------------------------------");

        _results = new List<VideoInfo>();
        var failedBvids = new List<string>();
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
                info.PubDate, info.View, isWhitelisted, isBlacklisted, _effectiveNow);

            info.DaysDiff = daysDiff;
            info.FilterReason = reason;

            if (!qualified)
            {
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
        _historySvc.LoadHistory();
        foreach (var r in sorted)
            _historySvc.SetEntry(r.Bvid, r.View, r.Like, r.Coin, r.Favorite);
        _historySvc.UpdateRanks(sorted.Select(r => r.Bvid).ToList());
        _historySvc.SaveHistory();
        Log($"已自动写入起算值: {sorted.Count} 个视频 → history.json（旧版已备份 .backup）");

        // 自动存档记录（筛选页数据来源）
        SaveRecord(sorted);

        TxtStatus.Text = failedBvids.Count > 0
            ? $"统计完成，共 {_results.Count} 个视频，起算值已自动保存（[!] {failedBvids.Count} 个获取失败，见日志）"
            : $"统计完成，共 {_results.Count} 个视频，起算值已自动保存";

        StatisticsCompleted?.Invoke();
    }

    /// <summary>把本次结果写入 Data/Records/记录_*.json（供筛选页选择）</summary>
    private void SaveRecord(List<VideoInfo> sorted)
    {
        try
        {
            var now = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var recordsDir = Path.Combine(_dataDir, "Records");
            Directory.CreateDirectory(recordsDir);
            var recordPath = Path.Combine(recordsDir, $"记录_{now}.json");
            var json = JsonSerializer.Serialize(sorted, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(recordPath, json, Encoding.UTF8);
            Log($"结果已自动记录: Records/记录_{now}.json");
        }
        catch (Exception ex)
        {
            Log($"[!] 存档记录失败: {ex.Message}");
        }
    }

    private void SetProgress(int current, int total)
    {
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
        // 同步写入日志文件（供「设置 → 日志查询」查看）
        LogService.Append(msg);

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            TxtLog.Text += msg + Environment.NewLine;
            TxtLog.CaretIndex = TxtLog.Text.Length;
        });
    }
}
