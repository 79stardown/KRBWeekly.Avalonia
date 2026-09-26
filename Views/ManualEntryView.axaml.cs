using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KRBWeekly.Helpers;
using KRBWeekly.Models;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

public partial class ManualEntryView : UserControl
{
    /// <summary>「不折算」口径代表值：days≥16 时 A 已 clamp 到 AMin，取 40 语义明确</summary>
    private const double NoDiscountDays = 40.0;

    private readonly ConfigService _configSvc;
    private readonly ManualEntryService _entrySvc;
    private readonly ManualExportService _exportSvc;
    private List<ManualEntry> _entries = new();
    private int _nextNumber = 1;
    private int? _editingNumber;   // null = 新增模式

    public ObservableCollection<ManualDisplay> Items { get; } = new();

    public ManualEntryView()
    {
        InitializeComponent();

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppContext.BaseDirectory;
        var dataDir = Path.Combine(exeDir, "Data");
        Directory.CreateDirectory(dataDir);

        _configSvc = new ConfigService(dataDir);
        _entrySvc = new ManualEntryService(dataDir);
        _exportSvc = new ManualExportService();

        Loaded += (_, _) =>
        {
            LoadEntries();
            ResetForm();
        };
    }

    private void LoadEntries()
    {
        (_entries, _nextNumber) = _entrySvc.Load();
        RefreshList();
        TxtCount.Text = $"共 {_entries.Count} 个数值组";
    }

    private void RefreshList()
    {
        Items.Clear();
        var sorted = _entries.OrderByDescending(e => e.TotalScore).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            Items.Add(new ManualDisplay
            {
                EntryRef = e,
                Rank = i + 1,
                DisplayName = e.DisplayName,
                NumberStr = $"编号 {e.Number}",
                TotalScoreStr = e.TotalScore.ToString("F2"),
                DeltaViewStr = $"{e.DeltaView:N0}",
                DeltaFavoriteStr = $"{e.DeltaFavorite:N0}",
                DeltaLikeStr = $"{e.DeltaLike:N0}",
                DeltaCoinStr = $"{e.DeltaCoin:N0}",
                DetailStr = $"系数 A={e.CoefA:F4} B={e.CoefB:F4} C={e.CoefC:F4}  |  " +
                            $"播放分 {e.PlayScore:F1} · 收藏分 {e.FavScore:F1} · 赞币分 {e.LikeCoinScore:F1}  |  " +
                            $"发布天数 {e.PublishDaysStr}"
            });
        }
        EntryList.ItemsSource = Items;
    }

    private void ResetForm()
    {
        _editingNumber = null;
        TxtFormTitle.Text = "新增数值组";
        TxtNumber.Text = _nextNumber.ToString();
        TxtCode.Text = "";
        TxtView.Text = "0";
        TxtFav.Text = "0";
        TxtLike.Text = "0";
        TxtCoin.Text = "0";
        TxtDays.Text = "";
        BtnAdd.IsVisible = true;
        BtnUpdate.IsVisible = false;
        BtnCancelEdit.IsVisible = false;
        ClearPreview();
    }

    private void ClearPreview()
    {
        TxtCoefs.Text = "系数 A=-- B=-- C=--";
        TxtBreakdown.Text = "播放分 -- | 收藏分 -- | 赞币分 --";
        TxtPreviewTotal.Text = "总分 --";
    }

    private void BtnCancelEdit_Click(object? sender, RoutedEventArgs e) => ResetForm();

    private async void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var view, out var fav, out var like, out var coin, out var days))
            return;

        var entry = new ManualEntry
        {
            Number = _nextNumber,
            Code = TxtCode.Text?.Trim() ?? "",
            DeltaView = view,
            DeltaFavorite = fav,
            DeltaLike = like,
            DeltaCoin = coin,
            PublishDays = days
        };
        ApplyScore(entry);
        ShowPreview(entry);

        if (entry.TotalScore <= 0)
        {
            await UiDialog.WarnAsync("提示", "该数值组的总分为 0，仍会记录，但不会排在榜单前列。");
        }

        _entries.Add(entry);
        _nextNumber++;
        _entrySvc.Save(_entries, _nextNumber);
        RefreshList();
        TxtCount.Text = $"共 {_entries.Count} 个数值组";

        var savedName = entry.DisplayName;
        ResetForm();
        TxtStatusHint($"已添加：{savedName}（编号 {entry.Number}）");
    }

    private async void BtnUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingNumber is not int num) return;
        var entry = _entries.FirstOrDefault(x => x.Number == num);
        if (entry == null) { ResetForm(); return; }

        if (!TryReadForm(out var view, out var fav, out var like, out var coin, out var days))
            return;

        entry.Code = TxtCode.Text?.Trim() ?? "";
        entry.DeltaView = view;
        entry.DeltaFavorite = fav;
        entry.DeltaLike = like;
        entry.DeltaCoin = coin;
        entry.PublishDays = days;
        ApplyScore(entry);

        _entrySvc.Save(_entries, _nextNumber);
        RefreshList();
        ResetForm();
        TxtStatusHint($"已保存修改：{entry.DisplayName}（编号 {entry.Number}）");
        await Task.CompletedTask;
    }

    private async void BtnEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManualDisplay d }) return;
        var entry = d.EntryRef;

        _editingNumber = entry.Number;
        TxtFormTitle.Text = $"编辑数值组（编号 {entry.Number}，编号不变）";
        TxtNumber.Text = entry.Number.ToString();
        TxtCode.Text = entry.Code;
        TxtView.Text = entry.DeltaView.ToString();
        TxtFav.Text = entry.DeltaFavorite.ToString();
        TxtLike.Text = entry.DeltaLike.ToString();
        TxtCoin.Text = entry.DeltaCoin.ToString();
        TxtDays.Text = entry.PublishDays is null or <= 0 ? "" : entry.PublishDays.Value.ToString("F1");
        BtnAdd.IsVisible = false;
        BtnUpdate.IsVisible = true;
        BtnCancelEdit.IsVisible = true;
        ShowPreview(entry);

        await Task.CompletedTask;
    }

    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManualDisplay d }) return;
        var entry = d.EntryRef;

        var ok = await UiDialog.ConfirmAsync("删除确认",
            $"确认删除「{entry.DisplayName}」（编号 {entry.Number}）？\n\n总分 {entry.TotalScore:F2}");
        if (!ok) return;

        _entries.RemoveAll(x => x.Number == entry.Number);
        _entrySvc.Save(_entries, _nextNumber);
        if (_editingNumber == entry.Number) ResetForm();
        RefreshList();
        TxtCount.Text = $"共 {_entries.Count} 个数值组";
        TxtStatusHint($"已删除：{entry.DisplayName}（编号 {entry.Number}，编号不再复用）");
    }

    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            await UiDialog.WarnAsync("提示", "还没有任何数值组。");
            return;
        }

        var sorted = _entries.OrderByDescending(x => x.TotalScore).ToList();
        try
        {
            var dir = _exportSvc.ExportList(sorted);
            await UiDialog.InfoAsync("导出成功", $"已导出 {sorted.Count} 个数值组到:\n{dir}");
        }
        catch (Exception ex)
        {
            await UiDialog.ErrorAsync("错误", $"导出失败: {ex.Message}");
        }
    }

    /// <summary>读取表单并校验；失败弹提示并返回 false</summary>
    private bool TryReadForm(out long view, out long fav, out long like, out long coin, out double? days)
    {
        view = fav = like = coin = 0;
        days = null;

        if (!TryParseNonNegative(TxtView.Text, out view) ||
            !TryParseNonNegative(TxtFav.Text, out fav) ||
            !TryParseNonNegative(TxtLike.Text, out like) ||
            !TryParseNonNegative(TxtCoin.Text, out coin))
        {
            _ = UiDialog.WarnAsync("输入错误", "新增播放/收藏/点赞/投币请填写非负整数。");
            return false;
        }

        var daysText = TxtDays.Text?.Trim();
        if (!string.IsNullOrEmpty(daysText))
        {
            if (!double.TryParse(daysText, out var d) || d < 0)
            {
                _ = UiDialog.WarnAsync("输入错误", "发布天数请填写非负数字，或留空表示不折算。");
                return false;
            }
            days = d;
        }
        return true;
    }

    private static bool TryParseNonNegative(string? text, out long value)
    {
        value = 0;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return true;   // 空 = 0
        if (!long.TryParse(t, out var v) || v < 0) return false;
        value = v;
        return true;
    }

    /// <summary>按当前系数配置计分（与自动模式同一套公式）</summary>
    private void ApplyScore(ManualEntry entry)
    {
        var config = _configSvc.Load();
        var days = entry.PublishDays is > 0 ? entry.PublishDays.Value : NoDiscountDays;
        var r = ScoreCalculator.CalcScore(
            entry.DeltaView, entry.DeltaFavorite, entry.DeltaLike, entry.DeltaCoin, days, config);

        entry.TotalScore = r.Total;
        entry.CoefA = r.CoefA;
        entry.CoefB = r.CoefB;
        entry.CoefC = r.CoefC;
        entry.PlayScore = r.PlayScore;
        entry.FavScore = r.FavScore;
        entry.LikeCoinScore = r.LikeCoinScore;
    }

    private void ShowPreview(ManualEntry entry)
    {
        TxtCoefs.Text = $"系数 A={entry.CoefA:F4} B={entry.CoefB:F4} C={entry.CoefC:F4}";
        TxtBreakdown.Text = $"播放分 {entry.PlayScore:F1} | 收藏分 {entry.FavScore:F1} | " +
                            $"赞币分 {entry.LikeCoinScore:F1}";
        TxtPreviewTotal.Text = $"总分 {entry.TotalScore:F2}";
    }

    private void TxtStatusHint(string text)
    {
        // 复用预览区当作轻量状态提示（无独立状态栏，避免与统计页占位重复）
        TxtPreviewTotal.Text = text;
    }
}

public class ManualDisplay : INotifyPropertyChanged
{
    public ManualEntry EntryRef { get; set; } = new();
    public int Rank { get; set; }
    public string DisplayName { get; set; } = "";
    public string NumberStr { get; set; } = "";
    public string TotalScoreStr { get; set; } = "";
    public string DeltaViewStr { get; set; } = "";
    public string DeltaFavoriteStr { get; set; } = "";
    public string DeltaLikeStr { get; set; } = "";
    public string DeltaCoinStr { get; set; } = "";
    public string DetailStr { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
