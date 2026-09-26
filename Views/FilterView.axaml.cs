using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KRBWeekly.Helpers;
using KRBWeekly.Models;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

public partial class FilterView : UserControl
{
    private readonly string _recordsDir;
    private readonly ExportService _exportSvc;
    private List<VideoInfo> _allResults = new();

    public ObservableCollection<FilterDisplay> Items { get; } = new();

    /// <summary>「← 返回统计」按钮回调（MainWindow 订阅后切回统计视图）</summary>
    public event Action? BackRequested;

    public FilterView()
    {
        InitializeComponent();

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppContext.BaseDirectory;
        _recordsDir = Path.Combine(exeDir, "Data", "Records");
        _exportSvc = new ExportService();

        Loaded += (_, _) => LoadRecordList();
    }

    /// <summary>刷新记录下拉并自动选中最新一条（统计完成自动切换后调用）</summary>
    public void SelectNewestRecord()
    {
        LoadRecordList();
        // 第一条是 "-- 请选择一次记录 --"，最新记录排第二（倒序）
        if (CbRecords.Items.Count > 1)
            CbRecords.SelectedIndex = 1;
    }

    private void BtnBack_Click(object? sender, RoutedEventArgs e)
        => BackRequested?.Invoke();

    private void LoadRecordList()
    {
        // 保留原选中项：统计完成自动切换时 SelectNewestRecord 先选好最新记录，
        // 随后 Loaded 还会再触发一次本方法；无条件归零会把刚载入的卡片列表清空
        // （SelectionChanged 收到 index<=0 就 Items.Clear + ItemsSource=null）。
        var prev = CbRecords.SelectedIndex;

        CbRecords.Items.Clear();
        CbRecords.Items.Add("-- 请选择一次记录 --");

        if (!Directory.Exists(_recordsDir))
            return;

        // 列出所有记录文件，按时间倒序
        var files = Directory.GetFiles(_recordsDir, "记录_*.json")
            .OrderByDescending(f => f)
            .ToList();

        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            // 把 "记录_20260805_143200" 格式化为更友好的显示
            var display = name.Replace("记录_", "").Replace("_", " ");
            CbRecords.Items.Add(display);
        }

        CbRecords.SelectedIndex = prev >= 0 && prev < CbRecords.Items.Count ? prev : 0;
    }

    private void CbRecords_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CbRecords.SelectedIndex <= 0) // "-- 请选择 --"
        {
            Items.Clear();
            CardList.ItemsSource = null;
            UpdateButtonState();   // 否则按钮上会留着上一次的 "导出勾选的 (N)"
            return;
        }

        var selected = CbRecords.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)) return;

        var fileName = "记录_" + selected!.Replace(" ", "_") + ".json";
        var filePath = Path.Combine(_recordsDir, fileName);

        if (!File.Exists(filePath))
        {
            Items.Clear();
            CardList.ItemsSource = null;
            return;
        }

        LoadRecord(filePath);
    }

    private void LoadRecord(string filePath)
    {
        Items.Clear();
        _allResults.Clear();

        try
        {
            var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            _allResults = JsonSerializer.Deserialize<List<VideoInfo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            var top45 = _allResults.OrderByDescending(r => r.TotalScore).Take(45).ToList();

            for (int i = 0; i < top45.Count; i++)
            {
                var r = top45[i];
                var display = new FilterDisplay
                {
                    IsChecked = true,
                    Rank = i + 1,
                    RankChangeShort = r.RankChangeShort,
                    Title = r.Title,
                    Bvid = r.Bvid,
                    CoverUrl = string.IsNullOrEmpty(r.CoverUrl)
                        ? "https://i0.hdslb.com/bfs/archive/placeholder.jpg"
                        : r.CoverUrl,
                    TotalScoreStr = r.TotalScore.ToString("F2"),
                    DeltaViewStr = $"{r.DeltaView:N0}",
                    DeltaFavoriteStr = $"{r.DeltaFavorite:N0}",
                    DeltaLikeStr = $"{r.DeltaLike:N0}",
                    DeltaCoinStr = $"{r.DeltaCoin:N0}",
                    // 不直接用存档里的 Status：旧版写入的是 "🆕 新上榜"/"📈 已存在" 这类带 emoji 的文本，
                    // 2.0 界面统一无 emoji。这里按 IsNew/IsWhitelist 重算，措辞与新统计写入的一致。
                    Status = r.IsNew
                        ? (r.IsWhitelist ? "新上榜 (白名单)" : "新上榜")
                        : (r.IsWhitelist ? "已存在 (白名单)" : "已存在")
                };
                display.PropertyChanged += (_, _) => UpdateButtonState();
                Items.Add(display);
            }

            CardList.ItemsSource = Items;
            UpdateButtonState();
        }
        catch (Exception ex)
        {
            _ = UiDialog.ErrorAsync("错误", $"加载失败: {ex.Message}");
        }
    }

    private void BtnSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsChecked = true;
        UpdateButtonState();
    }

    private void BtnDeselectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsChecked = false;
        UpdateButtonState();
    }

    private void UpdateButtonState()
    {
        var count = Items.Count(i => i.IsChecked);
        TxtExportLabel.Text = count > 0 ? $"导出勾选的 ({count})" : "导出勾选的";
    }

    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        var checkedBvids = Items.Where(i => i.IsChecked).Select(i => i.Bvid).ToHashSet();
        if (checkedBvids.Count == 0)
        {
            await UiDialog.WarnAsync("提示", "没有勾选任何视频。");
            return;
        }

        var toExport = _allResults
            .Where(r => checkedBvids.Contains(r.Bvid))
            .OrderByDescending(r => r.TotalScore)
            .ToList();

        try
        {
            var dir = _exportSvc.ExportTop25(toExport);
            await UiDialog.InfoAsync("导出成功", $"已导出 {checkedBvids.Count} 个视频到:\n{dir}");
        }
        catch (Exception ex)
        {
            await UiDialog.ErrorAsync("错误", $"导出失败: {ex.Message}");
        }
    }

    private async void BtnBlacklist_Click(object? sender, RoutedEventArgs e)
    {
        var uncheckedBvids = Items.Where(i => !i.IsChecked).Select(i => i.Bvid).ToList();
        if (uncheckedBvids.Count == 0)
        {
            await UiDialog.InfoAsync("提示", "没有未勾选的视频。");
            return;
        }

        var titles = Items.Where(i => !i.IsChecked)
            .Select(i => $"- {i.Title.Truncate(30)} ({i.Bvid})")
            .ToList();

        var confirmed = await UiDialog.ConfirmAsync("一键拉黑",
            $"确认将以下 {uncheckedBvids.Count} 个未勾选视频加入黑名单？\n\n" +
            string.Join("\n", titles.Take(10)) +
            (titles.Count > 10 ? $"\n... 等共 {titles.Count} 个" : ""));
        if (!confirmed) return;

        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                         ?? AppContext.BaseDirectory;
            var blackFile = Path.Combine(exeDir, "Data", "blacklist.txt");

            var existing = new HashSet<string>();
            if (File.Exists(blackFile))
            {
                foreach (var line in File.ReadAllLines(blackFile, System.Text.Encoding.UTF8))
                {
                    var bv = line.Trim().Split('#')[0].Trim();
                    if (!string.IsNullOrEmpty(bv) && !bv.StartsWith('#'))
                        existing.Add(bv);
                }
            }

            var added = 0;
            using var sw = new StreamWriter(blackFile, true, System.Text.Encoding.UTF8);
            foreach (var bvid in uncheckedBvids)
            {
                if (!existing.Contains(bvid))
                {
                    sw.WriteLine(bvid);
                    existing.Add(bvid);
                    added++;
                }
            }

            await UiDialog.InfoAsync("拉黑完成", $"已将 {added} 个未勾选视频加入黑名单。");
        }
        catch (Exception ex)
        {
            await UiDialog.ErrorAsync("错误", $"操作失败: {ex.Message}");
        }
    }

    private void Bvid_Click(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is string bvid)
        {
            Process.Start(new ProcessStartInfo($"https://www.bilibili.com/video/{bvid}")
            { UseShellExecute = true });
        }
    }
}

public class FilterDisplay : INotifyPropertyChanged
{
    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; OnPropertyChanged(); }
    }

    public int Rank { get; set; }
    public string RankChangeShort { get; set; } = "";
    public string Title { get; set; } = "";
    public string Bvid { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string TotalScoreStr { get; set; } = "";
    public string DeltaViewStr { get; set; } = "";
    public string DeltaFavoriteStr { get; set; } = "";
    public string DeltaLikeStr { get; set; } = "";
    public string DeltaCoinStr { get; set; } = "";
    public string Status { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
