using Avalonia.Controls;
using KRBWeekly.Helpers;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

/// <summary>
/// 标签下拉框：已有标签 + 末尾固定一项「＋ 新建标签…」。
/// 统计页、设置的黑白名单页、数据文件页共用同一套行为，避免三处各写一遍。
/// 切换/新建后会把当前标签写回 settings.json，并触发 <see cref="TagChanged"/>。
/// </summary>
public class TagSelector
{
    public const string NewItemText = "＋ 新建标签…";

    private readonly ComboBox _combo;
    private readonly string _dataDir;
    private readonly ConfigService _configSvc;
    private bool _loading;

    /// <summary>当前选中的标签（规整过的真实标签名，不会是「新建」那一项）</summary>
    public string CurrentTag { get; private set; } = "";

    /// <summary>标签变化后触发（参数是新标签）</summary>
    public event Action<string>? TagChanged;

    public TagSelector(ComboBox combo, string dataDir, ConfigService configSvc)
    {
        _combo = combo;
        _dataDir = dataDir;
        _configSvc = configSvc;
        _combo.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>重新灌下拉并选中标签（不触发 TagChanged，供页面初始化用）</summary>
    public void Reload(string? preferred = null)
    {
        var tag = TagService.Normalize(preferred);
        if (tag.Length == 0)
        {
            tag = TagService.Normalize(_configSvc.Load().SearchKeyword);
        }
        if (tag.Length == 0) tag = TagService.DefaultTag;

        TagService.EnsureRegistered(_dataDir, tag);
        FillItems(tag);
    }

    /// <summary>外部把标签改掉（比如另一处的下拉切换了）——只同步显示，不触发事件</summary>
    public void SyncSelection(string tag)
    {
        if (string.Equals(tag, CurrentTag, StringComparison.Ordinal)) return;
        var items = BuildItems(tag);
        SetItems(items, tag);
    }

    private List<string> BuildItems(string? selectTag)
    {
        var tags = TagService.LoadTags(_dataDir);
        var normalized = TagService.Normalize(selectTag);
        if (normalized.Length > 0 && !tags.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add(normalized);
        }
        return tags;
    }

    private void FillItems(string tag)
    {
        SetItems(BuildItems(tag), tag);
    }

    private void SetItems(List<string> tags, string? selectTag)
    {
        _loading = true;
        try
        {
            var items = new List<string>(tags) { NewItemText };
            _combo.ItemsSource = items;
            _combo.SelectedItem = tags.FirstOrDefault(t => string.Equals(t, selectTag, StringComparison.OrdinalIgnoreCase))
                                  ?? tags.FirstOrDefault();
            CurrentTag = _combo.SelectedItem as string ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (CbSelected() is not string selected || selected.Length == 0) return;

        if (selected == NewItemText)
        {
            await CreateNewAsync();
            return;
        }

        if (string.Equals(selected, CurrentTag, StringComparison.Ordinal)) return;

        CurrentTag = selected;
        SaveCurrentTag();
        TagChanged?.Invoke(CurrentTag);
    }

    private string? CbSelected() => _combo.SelectedItem as string;

    /// <summary>下拉里选了「＋ 新建标签…」</summary>
    private async Task CreateNewAsync()
    {
        var input = await UiDialog.PromptAsync("新建标签",
            "输入新的搜索标签（可以是别的游戏名 / 活动名）。\n新建后会立刻切换到它，起算值、统计记录和黑白名单都从零开始。",
            watermark: "如 KARDS金曲");

        if (input is null)
        {
            RestoreSelection();
            return;
        }

        if (!TagService.TryAddTag(_dataDir, input, out var tag, out var error))
        {
            await UiDialog.WarnAsync("无法新建", error ?? "标签不合法。");
            RestoreSelection();
            return;
        }

        FillItems(tag);
        CurrentTag = tag;
        SaveCurrentTag();
        TagChanged?.Invoke(tag);
    }

    private void RestoreSelection()
    {
        _loading = true;
        try
        {
            _combo.SelectedItem = CurrentTag;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveCurrentTag()
    {
        if (CurrentTag.Length == 0) return;

        var cfg = _configSvc.Load();
        if (string.Equals(cfg.SearchKeyword, CurrentTag, StringComparison.Ordinal)) return;

        cfg.SearchKeyword = CurrentTag;
        _configSvc.Save(cfg);
    }
}
