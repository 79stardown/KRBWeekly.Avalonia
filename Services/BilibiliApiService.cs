using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KRBWeekly.Models;

namespace KRBWeekly.Services;

/// <summary>
/// 一轮搜索的配置（关键词 + 排序方式 + 页数上限 + 可选的自适应停止条件）
/// </summary>
public class SearchRound
{
    public string Keyword { get; set; } = "";
    public string Order { get; set; } = "click";
    public int MaxPages { get; set; } = 50;
    /// <summary>pubdate 早于该时间的视频出现时停止本轮（用于「近 N 天新视频」轮）</summary>
    public DateTime? PubdateStopBefore { get; set; }
    public string Label { get; set; } = "";
}

public class BilibiliApiService
{
    // 页级请求结果状态
    private enum PageOutcome
    {
        Ok,            // 成功拿到一页数据
        EndOfResults,  // 正常结束本轮（空页 / 结构异常）
        SkipPage,      // 重试耗尽，跳过该页继续下一页
        AbortRound     // 触发风控(-412)，中止本轮
    }

    private sealed class PageData
    {
        public PageOutcome Outcome;
        public List<VideoInfo> Items = new();
        public int NumResults;
        public int NumPages;
    }

    private readonly HttpClient _http;
    private bool _cookieInitialized;

    public BilibiliApiService()
    {
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com/");
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 调试转储路径 —— 统一落在 Data\ 下。
    /// 不能写 exe 根目录：发布.bat 的清理项是 Data\search_debug*.json，
    /// 根目录的残留会被原样打进绿色包。
    /// </summary>
    private static string DebugDumpPath(string fileName)
    {
        var dir = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    /// <summary>
    /// 初始化 Cookie —— 先访问 Bilibili 首页获取会话 Cookie
    /// </summary>
    public async Task InitCookiesAsync(Action<string>? logCallback = null)
    {
        if (_cookieInitialized) return;

        try
        {
            logCallback?.Invoke("正在获取 Bilibili 会话 Cookie...");
            var resp = await _http.GetAsync("https://www.bilibili.com/");
            logCallback?.Invoke($"   首页访问: {(int)resp.StatusCode} {resp.StatusCode}");

            // 再访问一下搜索页面
            await Task.Delay(300);
            resp = await _http.GetAsync("https://search.bilibili.com/video?keyword=test");
            logCallback?.Invoke($"   搜索页访问: {(int)resp.StatusCode} {resp.StatusCode}");

            _cookieInitialized = true;
            logCallback?.Invoke("   Cookie 初始化完成");
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[!] Cookie 初始化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 多轮搜索编排 —— 按轮次依次搜索（跨轮去重），合并返回全部候选视频
    /// </summary>
    public async Task<List<VideoInfo>> SearchVideosAsync(
        IReadOnlyList<SearchRound> rounds, Action<string>? logCallback = null)
    {
        // 先确保有 Cookie
        await InitCookiesAsync(logCallback);
        if (!_cookieInitialized)
            logCallback?.Invoke("[!] Cookie 未获取，-412 风控风险升高");

        var videos = new List<VideoInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Bvid 去重（不区分大小写）
        var roundSummaries = new List<JsonObject>();

        logCallback?.Invoke($"搜索策略: {string.Join(" | ", rounds.Select(r => r.Label))}");
        logCallback?.Invoke("⏳ 候选视频较多，统计预计耗时数分钟，请耐心等待...");

        foreach (var round in rounds)
        {
            var summary = await SearchByOrderAsync(round, seen, videos, logCallback);
            roundSummaries.Add(summary);
        }

        logCallback?.Invoke($"搜索完成，合并去重后共 {videos.Count} 个视频");
        WriteSearchSummary(roundSummaries, videos.Count);
        return videos;
    }

    /// <summary>
    /// 单轮搜索 —— 按指定排序方式逐页抓取，带页级重试与自适应停止
    /// </summary>
    private async Task<JsonObject> SearchByOrderAsync(
        SearchRound round, HashSet<string> seen, List<VideoInfo> videos,
        Action<string>? logCallback)
    {
        int page = 1;
        int successPages = 0;
        int failedPages = 0;
        int consecutiveFailures = 0;
        int apiNumResults = 0;
        int apiNumPages = 0;
        string stopReason = "";
        bool adaptiveStop = false;

        logCallback?.Invoke($"  [{round.Label}] 开始搜索 (页上限 {round.MaxPages})");

        while (page <= round.MaxPages)
        {
            // 以 API 返回的总页数封顶，避免无效翻页
            if (apiNumPages > 0 && page > apiNumPages)
            {
                stopReason = $"已达 API 总页数 {apiNumPages}";
                break;
            }

            var url = $"https://api.bilibili.com/x/web-interface/search/type" +
                      $"?search_type=video&keyword={Uri.EscapeDataString(round.Keyword)}" +
                      $"&page={page}&order={round.Order}&pagesize=50";

            var pageData = await FetchSearchPageWithRetryAsync(url, page, logCallback);
            bool roundEnd = false;

            switch (pageData.Outcome)
            {
                case PageOutcome.Ok:
                    consecutiveFailures = 0;
                    successPages++;
                    if (pageData.NumResults > 0)
                    {
                        apiNumResults = pageData.NumResults;
                        apiNumPages = pageData.NumPages;
                    }

                    int added = 0;
                    foreach (var item in pageData.Items)
                    {
                        if (string.IsNullOrEmpty(item.Bvid)) continue;

                        // 自适应停止：pubdate 排序下遇到早于阈值的老视频即停（后续页只会更老）
                        if (round.PubdateStopBefore is { } stop &&
                            item.PubDate != DateTime.MinValue && item.PubDate < stop)
                        {
                            adaptiveStop = true;
                            stopReason = $"第 {page} 页出现 {item.PubDate:yyyy-MM-dd} 前发布的视频, 自适应停止";
                            break;
                        }

                        if (seen.Add(item.Bvid))
                        {
                            videos.Add(item);
                            added++;
                        }
                    }

                    logCallback?.Invoke($"   [{round.Label}] 第 {page} 页: {pageData.Items.Count} 条 (本轮新增 {added}, 去重后累计 {videos.Count})");
                    page++;
                    break;

                case PageOutcome.EndOfResults:
                    if (stopReason.Length == 0) stopReason = $"第 {page} 页无更多结果";
                    roundEnd = true;
                    break;

                case PageOutcome.SkipPage:
                    failedPages++;
                    consecutiveFailures++;
                    logCallback?.Invoke($"   [!] 第 {page} 页重试耗尽，跳过 (连续失败 {consecutiveFailures}/3)");
                    if (consecutiveFailures >= 3)
                    {
                        stopReason = "连续 3 页失败";
                        roundEnd = true;
                        break;
                    }
                    page++;
                    break;

                case PageOutcome.AbortRound:
                    stopReason = "触发风控 -412";
                    logCallback?.Invoke("   [!] 触发 B 站风控(-412)，本轮提前中止，建议稍后重试或更换网络");
                    roundEnd = true;
                    break;
            }

            if (roundEnd || adaptiveStop) break;
        }

        if (stopReason.Length == 0) stopReason = $"已达页上限 {round.MaxPages}";
        logCallback?.Invoke($"   [{round.Label}] 完成: API结果数={apiNumResults}, 总页数={apiNumPages}, 成功页 {successPages}, 失败页 {failedPages}, 停止原因: {stopReason}");
        if (round.PubdateStopBefore != null && apiNumResults >= 1000)
            logCallback?.Invoke("   [!] API 结果数达到 1000 上限, 可能存在更早的视频被截断");

        return new JsonObject
        {
            ["label"] = round.Label,
            ["keyword"] = round.Keyword,
            ["order"] = round.Order,
            ["pagesSucceeded"] = successPages,
            ["pagesFailed"] = failedPages,
            ["numResults"] = apiNumResults,
            ["numPages"] = apiNumPages,
            ["stopReason"] = stopReason,
        };
    }

    /// <summary>
    /// 抓取单页搜索结果 —— 最多 3 次尝试，-412 风控特殊退避；
    /// 重试耗尽后：风控 → 中止本轮；其它失败 → 跳过该页
    /// </summary>
    private async Task<PageData> FetchSearchPageWithRetryAsync(
        string url, int page, Action<string>? logCallback)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    logCallback?.Invoke($"   [!] 第 {page} 页 HTTP {(int)resp.StatusCode} (尝试 {attempt}/{maxAttempts})");
                    if (attempt < maxAttempts) { await Task.Delay(attempt == 1 ? 500 : 1500); continue; }
                    return new PageData { Outcome = PageOutcome.SkipPage };
                }

                var body = await resp.Content.ReadAsStringAsync();
                try { File.WriteAllText(DebugDumpPath("search_debug.json"), body); } catch { }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("code", out var codeEl))
                {
                    logCallback?.Invoke($"   [!] 响应无 code 字段，本轮结束");
                    return new PageData { Outcome = PageOutcome.EndOfResults };
                }

                var code = codeEl.GetInt32();
                if (code == -412)
                {
                    logCallback?.Invoke($"   [!] 触发 B 站风控 (-412) (尝试 {attempt}/{maxAttempts})");
                    if (attempt < maxAttempts) { await Task.Delay(attempt == 1 ? 3000 : 6000); continue; }
                    return new PageData { Outcome = PageOutcome.AbortRound };
                }
                if (code != 0)
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "未知";
                    logCallback?.Invoke($"   [!] API 错误 (code={code}): {msg} (尝试 {attempt}/{maxAttempts})");
                    if (attempt < maxAttempts) { await Task.Delay(attempt == 1 ? 500 : 1500); continue; }
                    return new PageData { Outcome = PageOutcome.SkipPage };
                }

                if (!root.TryGetProperty("data", out var dataEl) ||
                    !dataEl.TryGetProperty("result", out var resultEl))
                {
                    logCallback?.Invoke($"   [!] 响应结构异常，本轮结束");
                    return new PageData { Outcome = PageOutcome.EndOfResults };
                }

                var count = resultEl.GetArrayLength();
                if (count == 0)
                    return new PageData { Outcome = PageOutcome.EndOfResults };

                var data = new PageData { Outcome = PageOutcome.Ok };
                data.NumResults = dataEl.TryGetProperty("numResults", out var nr) ? nr.GetInt32() : 0;
                data.NumPages = dataEl.TryGetProperty("numPages", out var np) ? np.GetInt32() : 0;

                foreach (var item in resultEl.EnumerateArray())
                {
                    var bvid = item.TryGetProperty("bvid", out var bv) ? bv.GetString() ?? "" : "";
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var pic = item.TryGetProperty("pic", out var p) ? p.GetString() ?? "" : "";
                    // 搜索项自带 play 与 pubdate，供详情预过滤使用
                    var play = GetLongField(item, "play");
                    var pubdate = GetLongField(item, "pubdate");
                    data.Items.Add(new VideoInfo
                    {
                        Bvid = bvid,
                        Title = StripHtml(title),
                        CoverUrl = pic,
                        PlayFromSearch = play,
                        PubDate = pubdate > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime
                            : DateTime.MinValue
                    });
                }
                return data;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"   [!] 第 {page} 页出错: {ex.GetType().Name}: {ex.Message} (尝试 {attempt}/{maxAttempts})");
                if (attempt < maxAttempts) { await Task.Delay(attempt == 1 ? 500 : 1500); continue; }
                return new PageData { Outcome = PageOutcome.SkipPage };
            }
        }

        return new PageData { Outcome = PageOutcome.SkipPage };
    }

    /// <summary>
    /// 读取 JSON 字段为 long（兼容数字/字符串两种 ValueKind），不存在或解析失败返回 -1
    /// </summary>
    private static long GetLongField(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var el)) return -1;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) return v;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var sv)) return sv;
        return -1;
    }

    public async Task<VideoInfo?> GetVideoInfoAsync(
        string bvid, int maxRetries = 2, Action<string>? logCallback = null)
    {
        var url = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";

        for (int attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            try
            {
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    if (attempt <= maxRetries)
                    {
                        logCallback?.Invoke($"   [!] {bvid} HTTP {(int)resp.StatusCode}，重试 {attempt}/{maxRetries}");
                        await Task.Delay(attempt == 1 ? 400 : 1200);
                        continue;
                    }
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                    ? codeEl.GetInt32() : (int?)null;

                if (code == -412)
                {
                    // 风控：等待较长时间后重试
                    if (attempt <= maxRetries)
                    {
                        logCallback?.Invoke($"   [!] {bvid} 触发风控(-412)，等待后重试 {attempt}/{maxRetries}");
                        await Task.Delay(3000);
                        continue;
                    }
                    return null;
                }
                if (code is null or not 0)
                {
                    // 确定性错误（视频不存在/被删除等），重试无意义
                    return null;
                }
                if (!root.TryGetProperty("data", out var dataEl))
                    return null;

                var stat = dataEl.GetProperty("stat");
                var title = dataEl.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var pubdate = dataEl.TryGetProperty("pubdate", out var pd) ? pd.GetInt64() : 0L;

                var pic = dataEl.TryGetProperty("pic", out var picEl) ? picEl.GetString() ?? "" : "";

                return new VideoInfo
                {
                    Bvid = bvid,
                    Title = StripHtml(title),
                    CoverUrl = pic,
                    View = stat.GetProperty("view").GetInt64(),
                    Like = stat.GetProperty("like").GetInt64(),
                    Coin = stat.GetProperty("coin").GetInt64(),
                    Favorite = stat.GetProperty("favorite").GetInt64(),
                    PubDate = DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime
                };
            }
            catch (Exception ex)
            {
                if (attempt <= maxRetries)
                {
                    logCallback?.Invoke($"   [!] {bvid} 获取失败: {ex.GetType().Name}，重试 {attempt}/{maxRetries}");
                    await Task.Delay(attempt == 1 ? 400 : 1200);
                    continue;
                }
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 写入本次搜索会话的摘要（各轮统计 + 去重总数）
    /// </summary>
    private void WriteSearchSummary(List<JsonObject> roundSummaries, int totalAfterDedup)
    {
        try
        {
            var path = DebugDumpPath("search_debug_summary.json");
            var obj = new JsonObject
            {
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["totalAfterDedup"] = totalAfterDedup,
                ["rounds"] = new JsonArray(roundSummaries.Select(r => (JsonNode)r).ToArray()),
            };
            File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                System.Text.Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// 把详情预过滤后的数量追加写入搜索摘要（供统计页调用）
    /// </summary>
    public void AppendPrefilterToSummary(int prefilterCount)
    {
        try
        {
            var path = DebugDumpPath("search_debug_summary.json");
            if (!File.Exists(path)) return;
            var node = JsonNode.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)) as JsonObject;
            if (node == null) return;
            node["prefilteredAfterDetailFilter"] = prefilterCount;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                System.Text.Encoding.UTF8);
        }
        catch { }
    }

    public static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        input = WebUtility.HtmlDecode(input);
        input = Regex.Replace(input, "<[^>]+>", "");
        return input.Trim();
    }
}
