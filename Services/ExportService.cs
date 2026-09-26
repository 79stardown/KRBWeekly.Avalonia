using System.IO;
using System.Text;
using KRBWeekly.Models;

namespace KRBWeekly.Services;

/// <summary>
/// 榜单导出 —— Top 25 CSV + TXT，与 V3 格式兼容
/// </summary>
public class ExportService
{
    /// <summary>
    /// 导出 Top 25 到 CSV 和 TXT，返回导出文件夹路径
    /// </summary>
    public string ExportTop25(List<VideoInfo> sortedResults, string? outputDir = null)
    {
        var top25 = sortedResults.Take(25).ToList();
        if (top25.Count == 0)
            throw new InvalidOperationException("没有数据可导出");

        var timestamp = DateTime.Now.ToString("yyyyMMdd");
        var exportDir = outputDir ?? Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory,
            $"榜单导出_{timestamp}");

        if (!Directory.Exists(exportDir))
            Directory.CreateDirectory(exportDir);

        // 导出 CSV
        var csvPath = Path.Combine(exportDir, $"KARDS金曲周榜_前25_{timestamp}.csv");
        ExportCsv(top25, csvPath);

        // 导出 TXT
        var txtPath = Path.Combine(exportDir, $"KARDS金曲周榜_前25_{timestamp}.txt");
        ExportTxt(top25, txtPath);

        return exportDir;
    }

    private static void ExportCsv(List<VideoInfo> top25, string path)
    {
        var sb = new StringBuilder();
        // BOM 由 File.WriteAllText 的 Encoding.UTF8 前导码写入（Excel 兼容），勿再手动追加
        sb.AppendLine("排名,变化,标题,BV号,总分,状态,新增播放,新增收藏,新增点赞,新增投币,播放分,收藏分,赞币分");

        for (int i = 0; i < top25.Count; i++)
        {
            var r = top25[i];
            var title = EscapeCsv(r.Title);
            sb.AppendLine($"{i + 1},{r.RankChangeShort},{title},{r.Bvid}," +
                $"{r.TotalScore:F2},{r.Status}," +
                $"{r.DeltaView},{r.DeltaFavorite},{r.DeltaLike},{r.DeltaCoin}," +
                $"{r.PlayScore:F1},{r.FavScore:F1},{r.LikeCoinScore:F1}");
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void ExportTxt(List<VideoInfo> top25, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=======================================================");
        sb.AppendLine($"  KARDS金曲周榜 TOP25  {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine("=======================================================");
        sb.AppendLine();

        for (int i = 0; i < top25.Count; i++)
        {
            var r = top25[i];
            sb.AppendLine($"{i + 1,2}. {r.RankChange}  {r.Title}");
            sb.AppendLine($"    BV号：{r.Bvid}");
            sb.AppendLine($"    总分：{r.TotalScore:F2} | 状态：{r.Status}");
            sb.AppendLine($"    新增：播放+{r.DeltaView} 收藏+{r.DeltaFavorite} 点赞+{r.DeltaLike} 投币+{r.DeltaCoin}");
            sb.AppendLine();
        }

        sb.AppendLine("=======================================================");
        sb.AppendLine($"共 {top25.Count} 个视频");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string text)
    {
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n'))
        {
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }
        return text;
    }
}
