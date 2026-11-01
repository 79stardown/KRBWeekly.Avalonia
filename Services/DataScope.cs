using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KRBWeekly.Services;

/// <summary>
/// 按「搜索标签」隔离数据：每个标签一份历史起算值 + 一个记录目录，互不影响。
///
///   Data\History\&lt;标签&gt;.json     起算值（原来是单个 Data\history.json）
///   Data\Records\&lt;标签&gt;\记录_*.json  每次统计的结果存档
///
/// 标签名直接用作文件名（中文保留），只把非法字符替换成下划线；
/// 过长的标签会截断并补 8 位哈希，避免同名冲突。
/// </summary>
public static class DataScope
{
    public const string HistoryFolder = "History";
    public const string RecordsFolder = "Records";

    private const string HistoryMigratedMarker = ".history-migrated";
    private const string RecordsMigratedMarker = ".records-migrated";

    /// <summary>标签 → 安全的文件/目录名</summary>
    public static string FolderName(string? tag)
    {
        var t = (tag ?? "").Trim();
        if (t.Length == 0) return "default";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c) || c == '.' || Array.IndexOf(invalid, c) >= 0)
                sb.Append('_');
            else
                sb.Append(c);
        }

        var name = sb.ToString().Trim('_');
        if (name.Length == 0) name = "default";
        if (name.Length > 40) name = name[..32] + "_" + ShortHash(t);
        return name;
    }

    public static string HistoryFile(string dataDir, string? tag)
        => Path.Combine(dataDir, HistoryFolder, FolderName(tag) + ".json");

    public static string RecordsDirectory(string dataDir, string? tag)
        => Path.Combine(dataDir, RecordsFolder, FolderName(tag));

    private static string ShortHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// 旧版把起算值放在 Data\history.json、记录直接堆在 Data\Records\ 下。
    /// 升级后第一次运行按当前标签把它们挪进隔离目录（只做一次，靠标记文件去重）。
    /// 用「移动」而不是「复制」：新标签不会继承旧标签的起算值。
    /// </summary>
    public static void MigrateLegacy(string dataDir, string? tag, Action<string>? log = null)
    {
        MigrateHistory(dataDir, tag, log);
        MigrateRecords(dataDir, tag, log);
    }

    private static void MigrateHistory(string dataDir, string? tag, Action<string>? log)
    {
        try
        {
            var marker = Path.Combine(dataDir, HistoryMigratedMarker);
            if (File.Exists(marker)) return;

            var legacy = Path.Combine(dataDir, "history.json");
            if (!File.Exists(legacy)) return;

            var target = HistoryFile(dataDir, tag);
            if (File.Exists(target))
            {
                // 已经有分标签数据，旧文件不动（留着给用户自己判断）
                File.WriteAllText(marker, tag ?? "");
                return;
            }

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Move(legacy, target);

            var legacyBackup = legacy + ".backup";
            if (File.Exists(legacyBackup))
            {
                try { File.Move(legacyBackup, target + ".backup", true); } catch { /* 备份挪不动不影响使用 */ }
            }

            File.WriteAllText(marker, tag ?? "");
            log?.Invoke($"[i] 旧版起算值已迁移到标签「{tag}」：Data\\{HistoryFolder}\\{Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[!] 起算值迁移失败（继续使用旧文件）: {ex.Message}");
        }
    }

    private static void MigrateRecords(string dataDir, string? tag, Action<string>? log)
    {
        try
        {
            var marker = Path.Combine(dataDir, RecordsMigratedMarker);
            if (File.Exists(marker)) return;

            var root = Path.Combine(dataDir, RecordsFolder);
            if (!Directory.Exists(root)) return;

            var loose = Directory.GetFiles(root, "记录_*.json");
            if (loose.Length == 0) return;

            var targetDir = RecordsDirectory(dataDir, tag);
            Directory.CreateDirectory(targetDir);

            int moved = 0;
            foreach (var f in loose)
            {
                try
                {
                    File.Move(f, Path.Combine(targetDir, Path.GetFileName(f)));
                    moved++;
                }
                catch
                {
                    // 单个文件挪不动就跳过，不影响其它记录
                }
            }

            File.WriteAllText(marker, tag ?? "");
            if (moved > 0)
                log?.Invoke($"[i] 旧版记录已迁移到标签「{tag}」：Data\\{RecordsFolder}\\{FolderName(tag)}\\（{moved} 份）");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[!] 记录迁移失败: {ex.Message}");
        }
    }
}
