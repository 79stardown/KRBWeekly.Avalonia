using System.IO;

namespace KRBWeekly.Services;

/// <summary>
/// 白名单 / 黑名单管理（从 TXT 文件加载）
/// </summary>
public class ListService
{
    private readonly string _dataDir;

    public ListService(string dataDir)
    {
        _dataDir = dataDir;
    }

    /// <summary>
    /// 加载白名单 BV 号集合（修复：正确处理行内 # 注释）
    /// </summary>
    public HashSet<string> LoadWhitelist()
    {
        return LoadBvList(Path.Combine(_dataDir, "whitelist.txt"));
    }

    /// <summary>
    /// 加载黑名单 BV 号集合
    /// </summary>
    public HashSet<string> LoadBlacklist()
    {
        return LoadBvList(Path.Combine(_dataDir, "blacklist.txt"));
    }

    private static HashSet<string> LoadBvList(string filePath)
    {
        var set = new HashSet<string>();
        if (!File.Exists(filePath)) return set;

        foreach (var line in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.StartsWith('#')) continue;
            // 修复 V3 Bug: 正确处理行内 # 注释
            var bv = trimmed.Split('#')[0].Trim();
            if (!string.IsNullOrEmpty(bv))
                set.Add(bv);
        }
        return set;
    }
}
