using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KRBWeekly.Services;

/// <summary>
/// 搜索标签注册表（Data\tags.json）：标签不再靠手打，而是从已有标签里选或新建。
/// tags.json 缺失时会用「默认标签 + History 里已有的标签」兜底。
/// </summary>
public static class TagService
{
    public const string DefaultTag = "KARDS金曲";
    public const int MaxLength = 40;

    private const string FileName = "tags.json";

    private static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    private class TagFile
    {
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>规整标签：去首尾空白、去掉用户自带的外层引号、压缩连续空格</summary>
    public static string Normalize(string? raw)
    {
        var t = (raw ?? "").Trim();
        while (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
            t = t[1..^1].Trim();
        return Regex.Replace(t, @"\s+", " ");
    }

    public static List<string> LoadTags(string dataDir)
    {
        var tags = new List<string>();

        try
        {
            var p = PathFor(dataDir);
            if (File.Exists(p))
            {
                var doc = JsonSerializer.Deserialize<TagFile>(File.ReadAllText(p, Encoding.UTF8));
                if (doc?.Tags is { Count: > 0 })
                    foreach (var t in doc.Tags)
                        AddUnique(tags, Normalize(t));
            }
        }
        catch
        {
            // 配置损坏不影响使用，走下面的兜底
        }

        // 兜底 1：默认标签
        AddUnique(tags, DefaultTag);

        // 兜底 2：历史目录里已经存在的标签（老版本升级上来时 tags.json 还没有）
        try
        {
            var historyDir = Path.Combine(dataDir, DataScope.HistoryFolder);
            if (Directory.Exists(historyDir))
                foreach (var f in Directory.GetFiles(historyDir, "*.json"))
                    AddUnique(tags, Path.GetFileNameWithoutExtension(f));
        }
        catch
        {
            // 目录读不了就忽略
        }

        return tags;
    }

    public static void SaveTags(string dataDir, IEnumerable<string> tags)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var doc = new TagFile { Tags = tags.Select(Normalize).Where(t => t.Length > 0).Distinct().ToList() };
            File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch
        {
            // 写不进去不影响本次统计，只是下次要重新选
        }
    }

    /// <summary>
    /// 新建标签：校验空/超长/重名后追加到注册表。
    /// 返回 false 时 error 里是给用户看的原因。
    /// </summary>
    public static bool TryAddTag(string dataDir, string? raw, out string tag, out string? error)
    {
        var name = Normalize(raw);
        tag = name;
        error = null;

        if (name.Length == 0)
        {
            error = "标签不能为空。";
            return false;
        }
        if (name.Length > MaxLength)
        {
            error = $"标签最多 {MaxLength} 个字符。";
            return false;
        }

        var tags = LoadTags(dataDir);
        if (tags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"标签「{name}」已经存在，直接在上面的下拉里选它就行。";
            return false;
        }

        tags.Add(name);
        SaveTags(dataDir, tags);
        return true;
    }

    /// <summary>
    /// 从注册表移除标签（数据由调用方决定是否一并删除）。
    /// 至少保留一个标签，删到只剩一个时返回 false。
    /// </summary>
    public static bool TryRemoveTag(string dataDir, string? raw, out string? error)
    {
        var tag = Normalize(raw);
        error = null;

        var tags = LoadTags(dataDir);
        var existing = tags.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            error = $"标签「{tag}」不在列表里。";
            return false;
        }
        if (tags.Count <= 1)
        {
            error = "至少要保留一个标签，删除前先新建一个。";
            return false;
        }

        tags.Remove(existing);
        SaveTags(dataDir, tags);
        return true;
    }

    /// <summary>确保某个标签在注册表里（老配置/手工改过 settings.json 时用）</summary>
    public static void EnsureRegistered(string dataDir, string? raw)
    {
        var tag = Normalize(raw);
        if (tag.Length == 0) return;

        var tags = LoadTags(dataDir);
        if (tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) return;

        tags.Add(tag);
        SaveTags(dataDir, tags);
    }

    private static void AddUnique(List<string> tags, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) return;
        tags.Add(tag);
    }
}
