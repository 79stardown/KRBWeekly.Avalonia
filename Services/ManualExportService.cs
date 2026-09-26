using System.IO;
using System.Text;
using KRBWeekly.Models;

namespace KRBWeekly.Services;

/// <summary>
/// 手动榜导出 —— 全量 CSV + TXT（仿 ExportService 结构，与自动榜完全隔离）
/// </summary>
public class ManualExportService
{
    /// <summary>导出全部手动条目，返回导出文件夹路径</summary>
    public string ExportList(List<ManualEntry> sorted, string? outputDir = null)
    {
        if (sorted.Count == 0)
            throw new InvalidOperationException("没有数据可导出");

        var timestamp = DateTime.Now.ToString("yyyyMMdd");
        var exportDir = outputDir ?? Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            $"榜单导出_{timestamp}");

        if (!Directory.Exists(exportDir))
            Directory.CreateDirectory(exportDir);

        ExportCsv(sorted, Path.Combine(exportDir, $"手动榜单_全部_{timestamp}.csv"));
        ExportTxt(sorted, Path.Combine(exportDir, $"手动榜单_全部_{timestamp}.txt"));

        return exportDir;
    }

    private static void ExportCsv(List<ManualEntry> list, string path)
    {
        var sb = new StringBuilder();
        // BOM 由 File.WriteAllText 的 Encoding.UTF8 前导码写入（Excel 兼容），勿再手动追加
        sb.AppendLine("排名,编号,代号,总分,播放分,收藏分,赞币分,新增播放,新增收藏,新增点赞,新增投币,发布天数,系数A,系数B,系数C");

        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var daysStr = e.PublishDays is null or <= 0 ? "" : e.PublishDays.Value.ToString("F1");
            sb.AppendLine($"{i + 1},{e.Number},{EscapeCsv(e.DisplayName)}," +
                $"{e.TotalScore:F2},{e.PlayScore:F1},{e.FavScore:F1},{e.LikeCoinScore:F1}," +
                $"{e.DeltaView},{e.DeltaFavorite},{e.DeltaLike},{e.DeltaCoin}," +
                $"{daysStr},{e.CoefA:F4},{e.CoefB:F4},{e.CoefC:F4}");
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void ExportTxt(List<ManualEntry> list, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=======================================================");
        sb.AppendLine($"  KARDS金曲 手动榜单（输入-计算）  {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine("=======================================================");
        sb.AppendLine();

        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            sb.AppendLine($"{i + 1,2}. [{e.Number}] {e.DisplayName}");
            sb.AppendLine($"    总分：{e.TotalScore:F2}（系数 A={e.CoefA:F4} B={e.CoefB:F4} C={e.CoefC:F4}）");
            sb.AppendLine($"    分项：播放分 {e.PlayScore:F1} | 收藏分 {e.FavScore:F1} | 赞币分 {e.LikeCoinScore:F1}");
            sb.AppendLine($"    新增：播放+{e.DeltaView} 收藏+{e.DeltaFavorite} 点赞+{e.DeltaLike} 投币+{e.DeltaCoin}");
            sb.AppendLine($"    发布天数：{e.PublishDaysStr}");
            sb.AppendLine();
        }

        sb.AppendLine("=======================================================");
        sb.AppendLine($"共 {list.Count} 个数值组");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string text)
    {
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n'))
            return $"\"{text.Replace("\"", "\"\"")}\"";
        return text;
    }
}
