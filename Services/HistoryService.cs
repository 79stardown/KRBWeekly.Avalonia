using System.IO;
using System.Text.Json;
using KRBWeekly.Models;

namespace KRBWeekly.Services;

/// <summary>
/// 历史数据管理 —— history.json 读写 + 增量计算 + 排名追踪
///
/// 核心原则：
///   1. 统计时仅读取快照，不修改 history
///   2. 保存时才写入，并自动创建 .backup 副本
///   3. 与 V3 history.json 格式完全兼容
/// </summary>
public class HistoryService
{
    private readonly string _filePath;
    private Dictionary<string, HistoryEntry> _history = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public HistoryService(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "history.json");
    }

    public Dictionary<string, HistoryEntry> History => _history;

    /// <summary>
    /// 从磁盘加载历史数据
    /// </summary>
    public void LoadHistory()
    {
        if (!File.Exists(_filePath))
        {
            _history = new();
            return;
        }
        var json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
        _history = JsonSerializer.Deserialize<Dictionary<string, HistoryEntry>>(json, JsonOpts) ?? new();
    }

    /// <summary>
    /// 获取一份只读快照（用于统计计算，不影响实际 history）
    /// </summary>
    public Dictionary<string, HistoryEntry> GetSnapshot()
    {
        LoadHistory(); // 从磁盘加载最新数据
        // 深拷贝
        var json = JsonSerializer.Serialize(_history, JsonOpts);
        return JsonSerializer.Deserialize<Dictionary<string, HistoryEntry>>(json, JsonOpts) ?? new();
    }

    /// <summary>
    /// 基于快照计算增量（纯函数，不修改任何状态）
    ///
    /// 新上榜口径：发布超过 NewVideoFullDays 天的首进榜视频，增量按「总量 × 7/发布天数」
    /// （等效日均 × 7）折算为周增量，避免翻红老视频用全量虚高挤榜。
    /// 折算仅影响计分，不影响保存起算值（SetEntry 仍写真实总量）。
    /// </summary>
    private const double NewVideoFullDays = 14;

    public static (Dictionary<string, long> Delta, bool IsNew, int? LastRank, double DeltaFactor) ComputeDelta(
        Dictionary<string, HistoryEntry> snapshot,
        string bvid, long view, long like, long coin, long favorite,
        double? publishDays = null)
    {
        if (snapshot.TryGetValue(bvid, out var old))
        {
            var lastRank = old.LastRank > 0 ? old.LastRank : (int?)null;
            var delta = new Dictionary<string, long>
            {
                ["view"]   = view     - old.View,
                ["like"]   = like     - old.Like,
                ["coin"]   = coin     - old.Coin,
                ["favorite"] = favorite - old.Favorite
            };
            return (delta, false, lastRank, 1.0);
        }
        else
        {
            // 新上榜：发布较久的老视频按日均折算，新发布（≤14 天）用全量
            double factor = 1.0;
            if (publishDays is > NewVideoFullDays)
                factor = Math.Min(1.0, 7.0 / publishDays.Value);

            var delta = new Dictionary<string, long>
            {
                ["view"]     = (long)Math.Round(view * factor),
                ["like"]     = (long)Math.Round(like * factor),
                ["coin"]     = (long)Math.Round(coin * factor),
                ["favorite"] = (long)Math.Round(favorite * factor)
            };
            return (delta, true, null, factor);
        }
    }

    /// <summary>
    /// 保存历史数据（自动创建 .backup 副本）
    /// </summary>
    public void SaveHistory()
    {
        // 备份旧文件
        if (File.Exists(_filePath))
        {
            var backupPath = _filePath + ".backup";
            File.Copy(_filePath, backupPath, true);
        }

        var json = JsonSerializer.Serialize(_history, JsonOpts);
        File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// 更新指定视频的 last_rank
    /// </summary>
    public void UpdateRanks(List<string> sortedBvids)
    {
        for (int i = 0; i < sortedBvids.Count; i++)
        {
            var bvid = sortedBvids[i];
            if (_history.ContainsKey(bvid))
            {
                _history[bvid].LastRank = i + 1;
            }
        }
    }

    /// <summary>
    /// 将一个视频的当前数据写入 history（用于保存起算值）
    /// </summary>
    public void SetEntry(string bvid, long view, long like, long coin, long favorite)
    {
        if (_history.ContainsKey(bvid))
        {
            _history[bvid].View = view;
            _history[bvid].Like = like;
            _history[bvid].Coin = coin;
            _history[bvid].Favorite = favorite;
        }
        else
        {
            _history[bvid] = new HistoryEntry
            {
                View = view, Like = like, Coin = coin, Favorite = favorite
            };
        }
    }
}
