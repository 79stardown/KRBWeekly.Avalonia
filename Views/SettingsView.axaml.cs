using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KRBWeekly.Helpers;
using KRBWeekly.Models;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

public partial class SettingsView : UserControl
{
    private readonly string _dataDir;
    private readonly ConfigService _configSvc;
    private readonly BilibiliApiService _api;
    private readonly string _whiteFile;
    private readonly string _blackFile;
    private string? _selectedFile;

    public ObservableCollection<FileEntry> FileEntries { get; } = new();
    public ObservableCollection<FileEntry> LogEntries { get; } = new();
    public ObservableCollection<ListEntry> WhiteEntries { get; } = new();
    public ObservableCollection<ListEntry> BlackEntries { get; } = new();
    public ObservableCollection<RuleGroupVm> RuleGroups { get; } = new();

    public SettingsView()
    {
        InitializeComponent();

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppContext.BaseDirectory;
        _dataDir = Path.Combine(exeDir, "Data");
        Directory.CreateDirectory(_dataDir);
        _whiteFile = Path.Combine(_dataDir, "whitelist.txt");
        _blackFile = Path.Combine(_dataDir, "blacklist.txt");

        _configSvc = new ConfigService(_dataDir);
        _api = new BilibiliApiService();
        LogService.Init(_dataDir);

        FileList.ItemsSource = FileEntries;
        LogFileList.ItemsSource = LogEntries;
        WhiteList.ItemsSource = WhiteEntries;
        BlackList.ItemsSource = BlackEntries;
        RuleGroupList.ItemsSource = RuleGroups;

        Loaded += async (_, _) =>
        {
            LoadConfig();
            RefreshStatus();
            RefreshFileList();
            RefreshLogList();
            await LoadListsAsync();
        };
    }

    // 灌两个 ComboBox 时抑制 SelectionChanged 回写（否则每次进设置页都会触发一次保存）
    // 外观页与计算页共用这一个开关，故 LoadConfig 里一次性灌完
    private bool _loadingTabs;

    // ===== Tab 1: 外观 =====

    private void LoadAppearanceTab(ScoringConfig c)
    {
        CbThemeMode.SelectedIndex = ThemeService.IsDark(c.ThemeMode) ? 1 : 0;
        UpdateSwatchSelection(c.AccentColor);
    }

    /// <summary>选中色块加粗描边；AccentColor 为空表示默认蓝，故空值时高亮默认蓝色块。</summary>
    private void UpdateSwatchSelection(string? accentHex)
    {
        var current = string.IsNullOrWhiteSpace(accentHex) ? ThemeService.DefaultAccentHex : accentHex;

        foreach (var btn in AccentSwatches.Children.OfType<Button>())
        {
            var isCurrent = btn.Tag is string hex &&
                            string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            btn.BorderThickness = new Thickness(isCurrent ? 3 : 1);
        }

        TxtAccentHint.Text = $"当前: {current}";
    }

    private void ThemeMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingTabs) return;
        if (CbThemeMode.SelectedItem is not ComboBoxItem { Tag: string mode }) return;

        var c = _configSvc.Load();
        c.ThemeMode = mode;
        _configSvc.Save(c);
        ThemeService.ApplyThemeMode(mode);   // 立即切换，无需重启
    }

    private void CalcMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingTabs) return;
        if (CbCalcMode.SelectedItem is not ComboBoxItem { Tag: string mode }) return;

        var c = _configSvc.Load();
        c.CalcMode = mode;
        _configSvc.Save(c);
        UpdateCoefInputsEnabled(mode);
    }

    private void Swatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) ApplyAccent(hex);
    }

    private void ResetAccent_Click(object? sender, RoutedEventArgs e) => ApplyAccent("");

    /// <summary>改主题色：先落盘再应用 —— 应用失败也不至于丢设置。</summary>
    private void ApplyAccent(string hex)
    {
        var c = _configSvc.Load();
        c.AccentColor = hex;
        _configSvc.Save(c);
        ThemeService.ApplyAccent(hex);
        UpdateSwatchSelection(hex);
    }

    // ===== Tab 2: 计算（计算方式 + 评分系数） =====

    private void LoadCalcTab(ScoringConfig c)
    {
        CbCalcMode.SelectedIndex =
            string.Equals(c.CalcMode, "Simple", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        UpdateCoefInputsEnabled(c.CalcMode);
    }

    /// <summary>单纯相加根本不看 A/B/C：置灰输入框，免得填了数字却不参与计算。</summary>
    private void UpdateCoefInputsEnabled(string? calcMode)
    {
        var enabled = !string.Equals(calcMode, "Simple", StringComparison.OrdinalIgnoreCase);
        TxtAMax.IsEnabled = TxtADecay.IsEnabled = TxtAMin.IsEnabled = enabled;
        TxtBMult.IsEnabled = TxtBMax.IsEnabled = enabled;
        TxtCMult.IsEnabled = TxtCMax.IsEnabled = enabled;
    }

    private void LoadConfig()
    {
        _loadingTabs = true;
        try
        {
            var c = _configSvc.Load();
            TxtAMax.Text = c.AMax.ToString();
            TxtADecay.Text = c.ADecay.ToString();
            TxtAMin.Text = c.AMin.ToString();
            TxtBMult.Text = c.BMultiplier.ToString();
            TxtBMax.Text = c.BMax.ToString();
            TxtCMult.Text = c.CMultiplier.ToString();
            TxtCMax.Text = c.CMax.ToString();
            LoadAppearanceTab(c);
            LoadCalcTab(c);
            LoadRulesTab(c);
        }
        finally
        {
            _loadingTabs = false;
        }
    }

    private async void SaveConfig_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 先 Load 再改系数：直接 new 会把「外观」页的主题色/主题模式静默冲回默认
            var c = _configSvc.Load();
            c.AMax = ParseDouble(TxtAMax.Text, 1.4);
            c.ADecay = ParseDouble(TxtADecay.Text, 40);
            c.AMin = ParseDouble(TxtAMin.Text, 1.0);
            c.BMultiplier = ParseDouble(TxtBMult.Text, 500);
            c.BMax = ParseDouble(TxtBMax.Text, 40);
            c.CMultiplier = ParseDouble(TxtCMult.Text, 250);
            c.CMax = ParseDouble(TxtCMax.Text, 50);
            _configSvc.Save(c);
            await UiDialog.InfoAsync("成功", "系数已保存，下次统计/计算生效。");
        }
        catch
        {
            await UiDialog.ErrorAsync("错误", "请输入有效的数字。");
        }
    }

    private async void ResetConfig_Click(object? sender, RoutedEventArgs e)
    {
        // 只重置 7 个系数：同页的计算方式、「外观」页的主题设置、
        // 以及搜索标签/筛选规则都不该被这个按钮带走
        var cur = _configSvc.Load();
        var d = new ScoringConfig
        {
            CalcMode = cur.CalcMode,
            AccentColor = cur.AccentColor,
            ThemeMode = cur.ThemeMode,
            SearchKeyword = cur.SearchKeyword,
            FilterRules = cur.FilterRules,
        };
        _configSvc.Save(d);
        LoadConfig();
        await UiDialog.InfoAsync("完成", "已恢复默认系数。");
    }

    private static double ParseDouble(string? s, double defaultValue)
        => double.TryParse(s, out var v) ? v : defaultValue;

    // ===== Tab 2 下半部分: 筛选规则（已并入「计算与统计」） =====

    /// <summary>组间关系：true = 全部满足（All），false = 任一满足（Any）</summary>
    private bool _matchAll;

    private void LoadRulesTab(ScoringConfig c)
    {
        RuleGroups.Clear();

        var rules = c.FilterRules ?? FilterRuleSet.CreateDefault();
        TglRulesEnabled.IsChecked = rules.Enabled;
        _matchAll = string.Equals(rules.MatchMode, FilterMatch.All, StringComparison.OrdinalIgnoreCase);
        ApplySetModeToggles();

        foreach (var g in rules.Groups)
            RuleGroups.Add(RuleGroupVm.FromModel(g));

        RenumberGroups();
        UpdateRuleHint();
    }

    /// <summary>「组间」两个分段按钮的选中态跟 _matchAll 走（代码控制，避免 ToggleButton 互相取消）</summary>
    private void ApplySetModeToggles()
    {
        TglSetAny.IsChecked = !_matchAll;
        TglSetAll.IsChecked = _matchAll;
    }

    private void SetModeAny_Click(object? sender, RoutedEventArgs e)
    {
        _matchAll = false;
        ApplySetModeToggles();
        UpdateRuleHint();
    }

    private void SetModeAll_Click(object? sender, RoutedEventArgs e)
    {
        _matchAll = true;
        ApplySetModeToggles();
        UpdateRuleHint();
    }

    /// <summary>组标题跟着序号走：删掉中间一组后不留「规则组 1 / 3」这种空洞</summary>
    private void RenumberGroups()
    {
        for (int i = 0; i < RuleGroups.Count; i++)
            RuleGroups[i].Title = $"规则组 {i + 1}";
    }

    private void UpdateRuleHint()
    {
        if (TxtRuleHint == null) return;
        var enabled = TglRulesEnabled.IsChecked == true;
        var active = RuleGroups.Count(g => g.Enabled && g.Rules.Count > 0);
        var mode = _matchAll ? "全部满足" : "任一满足";

        TxtRuleHint.Text = enabled
            ? $"编辑中：{active} 个规则组生效（共 {RuleGroups.Count} 组，组间{mode}）。改动后需点「保存」才会生效。"
            : "规则集已关闭：不按内容筛选，仅黑白名单生效。改动后需点「保存」才会生效。";
    }

    private void RuleToggle_Changed(object? sender, RoutedEventArgs e) => UpdateRuleHint();

    private void AddGroup_Click(object? sender, RoutedEventArgs e)
    {
        var g = new RuleGroupVm();
        // 新组默认给一条「播放量 ≥ 0」，避免空组被当成无效组
        g.Rules.Add(new RuleVm { FieldIndex = FilterField.IndexOf(FilterField.View), MinText = "0" });
        RuleGroups.Add(g);
        RenumberGroups();
        UpdateRuleHint();
    }

    private void CopyGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RuleGroupVm g }) return;

        var index = RuleGroups.IndexOf(g);
        RuleGroups.Insert(index >= 0 ? index + 1 : RuleGroups.Count, g.Clone());
        RenumberGroups();
        UpdateRuleHint();
    }

    private void DeleteGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RuleGroupVm g }) return;
        RuleGroups.Remove(g);
        RenumberGroups();
        UpdateRuleHint();
    }

    private void AddRule_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RuleGroupVm g }) return;
        g.Rules.Add(new RuleVm { FieldIndex = FilterField.IndexOf(FilterField.View), MinText = "0" });
        UpdateRuleHint();
    }

    private void DeleteRule_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RuleVm rule }) return;
        foreach (var g in RuleGroups)
            if (g.Rules.Remove(rule)) break;
        UpdateRuleHint();
    }

    private async void SaveRules_Click(object? sender, RoutedEventArgs e)
    {
        var set = new FilterRuleSet
        {
            Enabled = TglRulesEnabled.IsChecked == true,
            MatchMode = _matchAll ? FilterMatch.All : FilterMatch.Any,
            Groups = new List<FilterRuleGroup>(),
        };

        for (int gi = 0; gi < RuleGroups.Count; gi++)
        {
            var gvm = RuleGroups[gi];
            var group = new FilterRuleGroup
            {
                Enabled = gvm.Enabled,
                MatchMode = gvm.MatchModeIndex == 0 ? FilterMatch.Any : FilterMatch.All,
            };

            for (int ri = 0; ri < gvm.Rules.Count; ri++)
            {
                if (!gvm.Rules[ri].TryToModel(out var rule, out var error))
                {
                    await UiDialog.WarnAsync("规则有误", $"规则组 {gi + 1} 第 {ri + 1} 条条件：{error}");
                    return;
                }
                group.Rules.Add(rule);
            }

            set.Groups.Add(group);
        }

        if (set.Groups.Count == 0)
        {
            var ok = await UiDialog.ConfirmAsync("规则集为空",
                "当前没有任何规则组，保存后等于不做内容筛选（所有视频直接进入计分）。确定保存？");
            if (!ok) return;
        }

        var c = _configSvc.Load();
        c.FilterRules = set;
        _configSvc.Save(c);

        UpdateRuleHint();
        await UiDialog.InfoAsync("已保存", $"筛选规则已保存（{set.Summary()}），下次统计生效。");
    }

    private async void ResetRules_Click(object? sender, RoutedEventArgs e)
    {
        var ok = await UiDialog.ConfirmAsync("恢复默认规则",
            "将把筛选规则恢复为默认的 A/B/C/D 四级过滤表。\n\n这会覆盖当前编辑的规则，继续？");
        if (!ok) return;

        var c = _configSvc.Load();
        c.FilterRules = FilterRuleSet.CreateDefault();
        _configSvc.Save(c);

        LoadRulesTab(c);
        await UiDialog.InfoAsync("完成", "已恢复默认筛选规则，下次统计生效。");
    }

    // ===== Tab 3: 黑白名单 =====

    private async Task LoadListsAsync()
    {
        await _api.InitCookiesAsync();
        await LoadListAsync(_whiteFile, WhiteEntries);
        await LoadListAsync(_blackFile, BlackEntries);
    }

    private async Task LoadListAsync(string filePath, ObservableCollection<ListEntry> entries)
    {
        entries.Clear();
        if (!File.Exists(filePath)) return;

        var bvids = new HashSet<string>();
        foreach (var line in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var bv = trimmed.Split('#')[0].Trim();
            if (!string.IsNullOrEmpty(bv)) bvids.Add(bv);
        }

        foreach (var bvid in bvids)
        {
            var info = await _api.GetVideoInfoAsync(bvid);
            entries.Add(new ListEntry
            {
                Bvid = bvid,
                Title = info?.Title ?? "(获取失败)",
                CoverUrl = info?.CoverUrl ?? ""
            });
        }
    }

    private async void BtnWhiteAdd_Click(object? sender, RoutedEventArgs e)
        => await AddToListAsync(TxtWhiteInput, WhiteEntries, _whiteFile, "白名单");

    private async void BtnBlackAdd_Click(object? sender, RoutedEventArgs e)
        => await AddToListAsync(TxtBlackInput, BlackEntries, _blackFile, "黑名单");

    private async Task AddToListAsync(TextBox input, ObservableCollection<ListEntry> entries,
        string filePath, string listName)
    {
        var bvid = input.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(bvid))
        {
            await UiDialog.WarnAsync("提示", "请输入 BV 号");
            return;
        }

        if (entries.Any(x => x.Bvid == bvid))
        {
            await UiDialog.WarnAsync("提示", $"该 BV 号已在{listName}中");
            return;
        }

        var info = await _api.GetVideoInfoAsync(bvid);
        entries.Add(new ListEntry
        {
            Bvid = bvid,
            Title = info?.Title ?? "(获取失败)",
            CoverUrl = info?.CoverUrl ?? ""
        });

        SaveList(filePath, entries);
        input.Text = "";
    }

    private void WhiteDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string bvid })
        {
            var entry = WhiteEntries.FirstOrDefault(x => x.Bvid == bvid);
            if (entry != null) WhiteEntries.Remove(entry);
            SaveList(_whiteFile, WhiteEntries);
        }
    }

    private void BlackDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string bvid })
        {
            var entry = BlackEntries.FirstOrDefault(x => x.Bvid == bvid);
            if (entry != null) BlackEntries.Remove(entry);
            SaveList(_blackFile, BlackEntries);
        }
    }

    private static void SaveList(string filePath, ObservableCollection<ListEntry> entries)
    {
        var lines = entries.Select(e => e.Bvid).ToList();
        // 保留注释行
        var comments = new List<string>();
        if (File.Exists(filePath))
        {
            foreach (var line in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
            {
                if (line.TrimStart().StartsWith('#'))
                    comments.Add(line);
            }
        }
        using var sw = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
        foreach (var bv in lines) sw.WriteLine(bv);
        foreach (var c in comments) sw.WriteLine(c);
    }

    private void Bvid_Click(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is string bvid)
        {
            Process.Start(new ProcessStartInfo($"https://www.bilibili.com/video/{bvid}")
            { UseShellExecute = true });
        }
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(_dataDir) { UseShellExecute = true });

    // ===== Tab 4: 数据文件（起算值备份 + 运行日志） =====

    /// <summary>当前搜索标签：起算值与记录都按标签隔离存放</summary>
    private string CurrentTag()
    {
        try { return _configSvc.Load().SearchKeyword; }
        catch { return ""; }
    }

    private string CurrentHistoryFile => DataScope.HistoryFile(_dataDir, CurrentTag());

    private void RefreshStatus()
    {
        var tag = CurrentTag();
        var historyFile = CurrentHistoryFile;

        if (File.Exists(historyFile))
        {
            var info = new FileInfo(historyFile);
            var count = HistoryService.CountEntries(historyFile);
            TxtCurrentStatus.Text = $"[OK] 标签「{tag}」 | History/{Path.GetFileName(historyFile)} | " +
                                    $"文件大小: {info.Length / 1024} KB | " +
                                    $"视频数: {(count < 0 ? "?" : count)} | " +
                                    $"最后修改: {info.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        else
        {
            TxtCurrentStatus.Text = $"[!] 标签「{tag}」还没有起算值" +
                                    $"（History/{Path.GetFileName(historyFile)} 不存在）";
        }
    }

    private void RefreshFileList()
    {
        FileEntries.Clear();

        var tag = CurrentTag();
        var historyFile = CurrentHistoryFile;
        var historyDir = Path.GetDirectoryName(historyFile);
        var historyName = Path.GetFileName(historyFile);

        var files = new List<string>();
        if (!string.IsNullOrEmpty(historyDir) && Directory.Exists(historyDir))
        {
            files.AddRange(Directory.GetFiles(historyDir, historyName + "*")
                .Where(f => !f.EndsWith(".backup.old")));
        }

        var recordsDir = DataScope.RecordsDirectory(_dataDir, tag);
        if (Directory.Exists(recordsDir))
            files.AddRange(Directory.GetFiles(recordsDir, "*.json"));

        foreach (var f in files.OrderByDescending(f => new FileInfo(f).LastWriteTime))
        {
            var info = new FileInfo(f);
            var fileName = Path.GetFileName(f);
            var isHistory = fileName.StartsWith(historyName);

            var displayName = isHistory
                ? (fileName == historyName ? $"{fileName}（当前 · 标签「{tag}」）" : $"备份 · {fileName}")
                : $"记录 · {fileName}";

            FileEntries.Add(new FileEntry
            {
                FilePath = f,
                DisplayName = displayName,
                SizeStr = FormatSize(info.Length),
                DateStr = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                CanRestore = isHistory && fileName != historyName
            });
        }

        TxtFileCount.Text = $"共 {FileEntries.Count} 个文件（标签「{tag}」）";
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filePath }) return;
        if (!File.Exists(filePath)) return;

        var ok = await UiDialog.ConfirmAsync("确认恢复",
            $"确认用此文件恢复当前标签「{CurrentTag()}」的起算值？\n\n{Path.GetFileName(filePath)}\n\n" +
            "当前文件将被备份为 .backup。");
        if (!ok) return;

        try
        {
            var dest = CurrentHistoryFile;
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            if (File.Exists(dest))
                File.Copy(dest, dest + ".backup", true);
            File.Copy(filePath, dest, true);

            RefreshStatus();
            RefreshFileList();
            await UiDialog.InfoAsync("完成", "恢复成功！");
        }
        catch (Exception ex)
        {
            await UiDialog.ErrorAsync("错误", $"恢复失败: {ex.Message}");
        }
    }

    private async void DeleteFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filePath }) return;
        if (!File.Exists(filePath)) return;

        var ok = await UiDialog.ConfirmAsync("确认删除",
            $"确认删除此文件？\n\n{Path.GetFileName(filePath)}\n\n此操作不可撤销（会移入回收站）。");
        if (!ok) return;

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                filePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            RefreshFileList();
        }
        catch (Exception ex)
        {
            await UiDialog.ErrorAsync("错误", $"删除失败: {ex.Message}");
        }
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 history.json 文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        var picked = files.FirstOrDefault();
        var localPath = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(localPath)) return;

        _selectedFile = localPath;
        TxtFileInfo.Text = $"已选择: {localPath}";

        try
        {
            var count = HistoryService.CountEntries(localPath);
            if (count < 0)
            {
                TxtFileInfo.Text += " | [!] 无法解析: 不是有效的起算值 JSON";
                BtnImport.IsEnabled = false;
                return;
            }

            TxtFileInfo.Text += $" | 包含 {count} 个视频";
            BtnImport.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TxtFileInfo.Text += $" | [!] 无法解析: {ex.Message}";
            BtnImport.IsEnabled = false;
        }
    }

    private async void BtnImport_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedFile) || !File.Exists(_selectedFile))
        {
            await UiDialog.WarnAsync("提示", "请先选择有效的 history.json 文件。");
            return;
        }

        var ok = await UiDialog.ConfirmAsync("确认导入",
            $"确认用选中文件替换当前标签「{CurrentTag()}」的起算值？\n\n文件: {_selectedFile}\n\n" +
            "当前文件会被备份为 .backup。");
        if (!ok) return;

        try
        {
            var destPath = CurrentHistoryFile;
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            if (File.Exists(destPath))
                File.Copy(destPath, destPath + ".backup", true);

            File.Copy(_selectedFile, destPath, true);

            RefreshStatus();
            RefreshFileList();
            TxtFileInfo.Text = "[OK] 导入成功！";
            BtnImport.IsEnabled = false;

            await UiDialog.InfoAsync("成功", "起算值已成功导入！");
        }
        catch (Exception ex)
        {
            await UiDialog.ErrorAsync("错误", $"导入失败: {ex.Message}");
        }
    }

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        RefreshStatus();
        RefreshFileList();
    }

    // ===== 运行日志（只读，已并入「数据文件」页） =====

    private void RefreshLogList()
    {
        LogEntries.Clear();
        var logDir = LogService.LogDir;
        if (!Directory.Exists(logDir))
        {
            TxtLogInfo.Text = "暂无日志（运行一次统计后自动生成）";
            return;
        }

        // 文件名含时间戳，按名称倒序即最新在前
        var files = Directory.GetFiles(logDir, "*.txt").OrderByDescending(f => f).ToList();
        foreach (var f in files)
        {
            var info = new FileInfo(f);
            LogEntries.Add(new FileEntry
            {
                FilePath = f,
                DisplayName = "" + Path.GetFileName(f),
                SizeStr = FormatSize(info.Length),
                DateStr = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                CanRestore = false
            });
        }
        TxtLogInfo.Text = $"共 {files.Count} 份日志";
    }

    private void LogFileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LogFileList.SelectedItem is not FileEntry entry) return;
        TxtLogViewer.Text = LoadLogContent(entry.FilePath);
    }

    /// <summary>只读加载日志内容（共享读不阻塞程序写入；超过 1MB 仅显示末尾）</summary>
    private static string LoadLogContent(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const long maxBytes = 1024 * 1024;
            var note = "";
            if (fs.Length > maxBytes)
            {
                fs.Seek(-maxBytes, SeekOrigin.End);
                note = $"（文件较大，仅显示末尾 {maxBytes / 1024} KB）\n\n";
            }
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            return note + reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"[!] 读取日志失败: {ex.Message}";
        }
    }

    private void RefreshLogList_Click(object? sender, RoutedEventArgs e) => RefreshLogList();

    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e)
    {
        var logDir = LogService.LogDir;
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

public class FileEntry
{
    public string FilePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SizeStr { get; set; } = "";
    public string DateStr { get; set; } = "";
    public bool CanRestore { get; set; }
}

public class ListEntry
{
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string CoverUrl { get; set; } = "";
}
