using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using KRBWeekly.Models;

namespace KRBWeekly.Services;

/// <summary>从表格里读出来的原始内容：第一行是表头，其余行按列索引对齐</summary>
public class TableData
{
    public string SourceName { get; set; } = "";
    public List<string> Headers { get; } = new();
    public List<List<string>> Rows { get; } = new();

    public int ColumnCount => Math.Max(Headers.Count, Rows.Count == 0 ? 0 : Rows.Max(r => r.Count));

    /// <summary>表头为空列时给个「第 N 列」，避免下拉框里出现空白项</summary>
    public string HeaderAt(int index)
        => index >= 0 && index < Headers.Count && !string.IsNullOrWhiteSpace(Headers[index])
            ? Headers[index].Trim()
            : $"第 {index + 1} 列";
}

/// <summary>
/// 表格读取（.xlsx / .csv / .tsv）。不引第三方包：xlsx 就是 zip + XML，自己解一下就够用。
/// </summary>
public static class TableImportService
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    static TableImportService()
    {
        try
        {
            // GBK/GB18030 需要显式注册（.NET 默认只带 UTF-8/UTF-16 等）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 运行环境没有 CodePages 时退回 UTF-8，不影响 xlsx
        }
    }

    public static TableData Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var data = ext switch
        {
            ".xlsx" => ReadXlsx(path),
            ".csv" or ".tsv" or ".txt" => ReadDelimitedFile(path),
            ".xls" => throw new NotSupportedException(
                "不支持旧版 .xls，请用 Excel 另存为 .xlsx 或 .csv 后再导入。"),
            _ => throw new NotSupportedException("只支持 .xlsx / .csv / .tsv 表格文件。"),
        };

        data.SourceName = Path.GetFileName(path);
        Normalize(data);
        return data;
    }

    // ===== 通用整理 =====

    /// <summary>去掉整行/整列都为空的内容，并把短行补到同一列数</summary>
    private static void Normalize(TableData data)
    {
        // 丢掉尾部空列
        while (data.ColumnCount > 0)
        {
            int last = data.ColumnCount - 1;
            if (!string.IsNullOrWhiteSpace(data.HeaderAt(last))) break;
            if (data.Rows.Any(r => last < r.Count && !string.IsNullOrWhiteSpace(r[last]))) break;
            if (last < data.Headers.Count) data.Headers.RemoveAt(last);
            foreach (var r in data.Rows)
                if (last < r.Count) r.RemoveAt(last);
        }

        // 丢掉空行
        data.Rows.RemoveAll(r => r.All(string.IsNullOrWhiteSpace));

        // 短行补齐，长行截断到列数
        var width = data.ColumnCount;
        foreach (var r in data.Rows)
        {
            while (r.Count < width) r.Add("");
            if (r.Count > width) r.RemoveRange(width, r.Count - width);
        }
    }

    // ===== CSV / TSV =====

    private static TableData ReadDelimitedFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = DecodeText(bytes);
        var delim = GuessDelimiter(text);
        var rows = ParseDelimited(text, delim);

        var data = new TableData();
        if (rows.Count == 0) return data;

        data.Headers.AddRange(rows[0].Select(TrimCell));
        foreach (var r in rows.Skip(1))
            data.Rows.Add(r.Select(TrimCell).ToList());
        return data;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // 先按 UTF-8 严格解，解不开再按 GB18030（Excel 另存的 CSV 默认是 GBK）
        try
        {
            var strict = new UTF8Encoding(false, true);
            return strict.GetString(bytes);
        }
        catch
        {
            try { return Encoding.GetEncoding(54936).GetString(bytes); } // GB18030
            catch { return Encoding.UTF8.GetString(bytes); }
        }
    }

    private static char GuessDelimiter(string text)
    {
        int comma = 0, tab = 0, semi = 0;
        int limit = Math.Min(text.Length, 4000);
        bool inQuotes = false;
        for (int i = 0; i < limit; i++)
        {
            char c = text[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes)
            {
                if (c == ',') comma++;
                else if (c == '\t') tab++;
                else if (c == ';') semi++;
            }
        }

        if (tab >= comma && tab >= semi && tab > 0) return '\t';
        if (semi > comma) return ';';
        return ',';
    }

    private static List<List<string>> ParseDelimited(string text, char delim)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(sb.ToString());
                    sb.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    if (c == delim)
                    {
                        row.Add(sb.ToString());
                        sb.Clear();
                    }
                    else sb.Append(c);
                    break;
            }
        }

        if (sb.Length > 0 || row.Count > 0)
        {
            row.Add(sb.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string TrimCell(string s) => s.Trim();

    // ===== XLSX =====

    private static TableData ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var shared = ReadSharedStrings(zip);
        var sheet = FindFirstSheet(zip)
                    ?? throw new InvalidDataException("这个 xlsx 里没找到工作表。");

        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);

        var data = new TableData();
        var allRows = new List<List<string>>();

        var rowElements = doc.Descendants(Ns + "row").ToList();
        foreach (var rowEl in rowElements)
        {
            var cells = new SortedDictionary<int, string>();

            foreach (var c in rowEl.Elements(Ns + "c"))
            {
                var reference = (string?)c.Attribute("r");
                int col = ColumnIndex(reference);
                if (col < 0) continue;

                var type = (string?)c.Attribute("t") ?? "n";
                string value;

                if (type == "s")
                {
                    var v = c.Element(Ns + "v")?.Value;
                    value = int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
                }
                else if (type == "inlineStr")
                {
                    var isEl = c.Element(Ns + "is");
                    value = isEl == null ? "" : string.Concat(isEl.Descendants(Ns + "t").Select(t => t.Value));
                }
                else
                {
                    value = c.Element(Ns + "v")?.Value ?? "";
                    if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                }

                cells[col] = value;
            }

            if (cells.Count == 0)
            {
                allRows.Add(new List<string>());
                continue;
            }

            int width = cells.Keys.Max() + 1;
            var list = new List<string>(width);
            for (int i = 0; i < width; i++)
                list.Add(cells.TryGetValue(i, out var v) ? v : "");
            allRows.Add(list);
        }

        if (allRows.Count == 0) return data;

        data.Headers.AddRange(allRows[0].Select(TrimCell));
        foreach (var r in allRows.Skip(1))
            data.Rows.Add(r.Select(TrimCell).ToList());
        return data;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return list;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Root!.Elements(Ns + "si"))
            list.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));
        return list;
    }

    private static ZipArchiveEntry? FindFirstSheet(ZipArchive zip)
    {
        // 优先 sheet1，再退而求其次取第一个工作表 XML（跳过 _rels 与 chartsheet）
        var direct = zip.GetEntry("xl/worksheets/sheet1.xml");
        if (direct != null) return direct;

        return zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>把 "B12" 这样的单元格地址换算成 0 基列号</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;

        int col = 0;
        int i = 0;
        for (; i < reference.Length; i++)
        {
            char c = char.ToUpperInvariant(reference[i]);
            if (c < 'A' || c > 'Z') break;
            col = col * 26 + (c - 'A' + 1);
        }

        return i == 0 ? -1 : col - 1;
    }

    // ===== 单元格解析（供导入逻辑复用） =====

    /// <summary>空单元格按 0；支持「1,234」「1.2e3」等写法；非数字返回 false</summary>
    public static bool TryParseLong(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var t = text.Trim().Replace(",", "").Replace(" ", "").Replace("，", "");
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            if (value < 0) value = 0;
            return true;
        }

        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d))
        {
            value = (long)Math.Round(Math.Max(d, 0));
            return true;
        }

        return false;
    }

    /// <summary>发布天数：空 = null（不折算）</summary>
    public static bool TryParseDays(string? text, out double? days)
    {
        days = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var t = text.Trim().Replace("天", "").Replace(" ", "");
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) ||
            double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
        {
            if (d < 0) d = 0;
            days = d;
            return true;
        }

        return false;
    }

    /// <summary>「不折算」口径代表值：days ≥ 16 时 A 已 clamp 到 AMin，取 40 语义明确</summary>
    public const double NoDiscountDays = 40.0;

    /// <summary>
    /// 按列映射把表格行转成 ManualEntry 并完成计分。
    /// 数值列（播放/收藏/点赞/投币/天数）任一解析失败 → 该行跳过并计入 skipped。
    /// </summary>
    public static (List<ManualEntry> Entries, int Skipped) BuildEntries(
        TableData table, IReadOnlyDictionary<string, int> mapping, ScoringConfig config, int startNumber)
    {
        var entries = new List<ManualEntry>();
        int skipped = 0;

        string Cell(List<string> row, string key)
            => mapping.TryGetValue(key, out var idx) && idx >= 0 && idx < row.Count ? row[idx] : "";

        foreach (var row in table.Rows)
        {
            if (!TryParseLong(Cell(row, "view"), out var view) ||
                !TryParseLong(Cell(row, "favorite"), out var fav) ||
                !TryParseLong(Cell(row, "like"), out var like) ||
                !TryParseLong(Cell(row, "coin"), out var coin) ||
                !TryParseDays(Cell(row, "days"), out var days))
            {
                skipped++;
                continue;
            }

            var entry = new ManualEntry
            {
                Number = startNumber + entries.Count,
                Code = Cell(row, "code").Trim(),
                DeltaView = view,
                DeltaFavorite = fav,
                DeltaLike = like,
                DeltaCoin = coin,
                PublishDays = days,
            };

            var scoreDays = entry.PublishDays is > 0 ? entry.PublishDays.Value : NoDiscountDays;
            var result = ScoreCalculator.CalcScore(
                entry.DeltaView, entry.DeltaFavorite, entry.DeltaLike, entry.DeltaCoin, scoreDays, config);
            entry.TotalScore = result.Total;
            entry.CoefA = result.CoefA;
            entry.CoefB = result.CoefB;
            entry.CoefC = result.CoefC;
            entry.PlayScore = result.PlayScore;
            entry.FavScore = result.FavScore;
            entry.LikeCoinScore = result.LikeCoinScore;

            entries.Add(entry);
        }

        return (entries, skipped);
    }
}
