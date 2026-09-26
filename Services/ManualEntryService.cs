using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using KRBWeekly.Models;

namespace KRBWeekly.Services;

/// <summary>
/// 手动条目持久化（Data/manual_entries.json）
/// 文件不存在时不创建，首次保存时生成（与 settings.json 同理，不随包分发）
/// </summary>
public class ManualEntryService
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ManualEntryService(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "manual_entries.json");
    }

    /// <summary>读取条目与下一个可用编号（文件缺失/损坏时返回空表）</summary>
    public (List<ManualEntry> Entries, int NextNumber) Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return (new List<ManualEntry>(), 1);

            var json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
            var doc = JsonSerializer.Deserialize<ManualEntryFile>(json, ReadOptions);
            if (doc == null)
                return (new List<ManualEntry>(), 1);

            var entries = doc.Entries ?? new List<ManualEntry>();
            var next = doc.NextNumber > 0
                ? doc.NextNumber
                : entries.Count == 0 ? 1 : entries.Max(e => e.Number) + 1;
            return (entries, next);
        }
        catch
        {
            // 读取失败不阻断使用，返回空表
            return (new List<ManualEntry>(), 1);
        }
    }

    public void Save(List<ManualEntry> entries, int nextNumber)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var doc = new ManualEntryFile { NextNumber = nextNumber, Entries = entries };
            var json = JsonSerializer.Serialize(doc, WriteOptions);
            File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 静默：调用方负责提示
        }
    }

    private class ManualEntryFile
    {
        [JsonPropertyName("nextNumber")]
        public int NextNumber { get; set; }

        [JsonPropertyName("entries")]
        public List<ManualEntry>? Entries { get; set; }
    }
}
