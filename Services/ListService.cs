using System.IO;

namespace KRBWeekly.Services;

/// <summary>
/// 白名单 / 黑名单管理（从 TXT 文件加载）。
/// 名单跟着搜索标签走：Data\Lists\&lt;标签&gt;\whitelist.txt、blacklist.txt。
/// </summary>
public class ListService
{
    private readonly string _dir;

    public ListService(string dataDir, string? tag)
    {
        _dir = DataScope.ListsDirectory(dataDir, tag);
    }

    public string WhitelistPath => Path.Combine(_dir, "whitelist.txt");
    public string BlacklistPath => Path.Combine(_dir, "blacklist.txt");

    /// <summary>加载白名单 BV 号集合（正确处理行内 # 注释）</summary>
    public HashSet<string> LoadWhitelist() => LoadBvList(WhitelistPath);

    /// <summary>加载黑名单 BV 号集合</summary>
    public HashSet<string> LoadBlacklist() => LoadBvList(BlacklistPath);

    public void SaveWhitelist(IEnumerable<string> bvids) => SaveBvList(WhitelistPath, bvids);

    public void SaveBlacklist(IEnumerable<string> bvids) => SaveBvList(BlacklistPath, bvids);

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

    /// <summary>写名单：保留原文件里的注释行，方便用户自己标注</summary>
    private static void SaveBvList(string filePath, IEnumerable<string> bvids)
    {
        var comments = new List<string>();
        if (File.Exists(filePath))
        {
            foreach (var line in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
            {
                if (line.TrimStart().StartsWith('#'))
                    comments.Add(line);
            }
        }

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var sw = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
        foreach (var bv in bvids) sw.WriteLine(bv);
        foreach (var c in comments) sw.WriteLine(c);
    }
}
