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
        // 只重置 7 个系数：同页的计算方式、以及「外观」页的主题设置都不该被这个按钮带走
        var cur = _configSvc.Load();
        var d = new ScoringConfig
        {
            CalcMode = cur.CalcMode,
            AccentColor = cur.AccentColor,
            ThemeMode = cur.ThemeMode,
        };
        _configSvc.Save(d);
        LoadConfig();
        await UiDialog.InfoAsync("完成", "已恢复默认系数。");
    }

    private static double ParseDouble(string? s, double defaultValue)
        => double.TryParse(s, out var v) ? v : defaultValue;

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

    private void RefreshStatus()
    {
        var historyFile = Path.Combine(_dataDir, "history.json");
        if (File.Exists(historyFile))
        {
            var info = new FileInfo(historyFile);
            var svc = new HistoryService(_dataDir);
            svc.LoadHistory();
            TxtCurrentStatus.Text = $"[OK] history.json 存在 | " +
                                    $"文件大小: {info.Length / 1024} KB | " +
                                    $"视频数: {svc.History.Count} | " +
                                    $"最后修改: {info.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        else
        {
            TxtCurrentStatus.Text = "[!] history.json 不存在（尚未保存过起算值）";
        }
    }

    private void RefreshFileList()
    {
        FileEntries.Clear();

        var files = new List<string>();
        files.AddRange(Directory.GetFiles(_dataDir, "history.json*")
            .Where(f => !f.EndsWith(".backup.old")));

        var recordsDir = Path.Combine(_dataDir, "Records");
        if (Directory.Exists(recordsDir))
            files.AddRange(Directory.GetFiles(recordsDir, "*.json"));

        foreach (var f in files.OrderByDescending(f => new FileInfo(f).LastWriteTime))
        {
            var info = new FileInfo(f);
            var isHistory = Path.GetFileName(f).StartsWith("history.json");
            var displayName = f.Contains("Records")
                ? "" + Path.GetFileName(f)
                : (Path.GetFileName(f) == "history.json" ? "history.json (当前)" : "" + Path.GetFileName(f));

            FileEntries.Add(new FileEntry
            {
                FilePath = f,
                DisplayName = displayName,
                SizeStr = FormatSize(info.Length),
                DateStr = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                CanRestore = isHistory && Path.GetFileName(f) != "history.json"
            });
        }

        TxtFileCount.Text = $"共 {FileEntries.Count} 个文件";
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filePath }) return;
        if (!File.Exists(filePath)) return;

        var ok = await UiDialog.ConfirmAsync("确认恢复",
            $"确认用此文件恢复当前的 history.json？\n\n{Path.GetFileName(filePath)}\n\n" +
            "当前 history.json 将被备份为 .backup。");
        if (!ok) return;

        try
        {
            var dest = Path.Combine(_dataDir, "history.json");
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
            var tempSvc = new HistoryService(Path.GetDirectoryName(localPath)!);
            tempSvc.LoadHistory();
            TxtFileInfo.Text += $" | 包含 {tempSvc.History.Count} 个视频";
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
            $"确认用选中文件替换当前的 history.json？\n\n文件: {_selectedFile}\n\n" +
            "当前文件会被备份为 .backup。");
        if (!ok) return;

        try
        {
            var destPath = Path.Combine(_dataDir, "history.json");
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
