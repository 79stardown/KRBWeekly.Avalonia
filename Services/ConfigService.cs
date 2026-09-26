using System.IO;
using System.Text.Json;
using KRBWeekly.Models;

namespace KRBWeekly.Services;

public class ConfigService
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public ConfigService(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "settings.json");
    }

    public ScoringConfig Load()
    {
        if (!File.Exists(_filePath))
            return new ScoringConfig();

        var json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
        return JsonSerializer.Deserialize<ScoringConfig>(json, JsonOpts) ?? new ScoringConfig();
    }

    public void Save(ScoringConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);
    }
}
