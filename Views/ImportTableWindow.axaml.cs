using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using KRBWeekly.Helpers;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

/// <summary>
/// 导入表格前的「列映射」窗口：用户把表格里的某一列指定给播放/收藏/点赞/投币/发布天数/代号，
/// 确认后由调用方按映射逐行计分。
/// </summary>
public partial class ImportTableWindow : Window
{
    private sealed class FieldDef
    {
        public string Key = "";
        public string Label = "";
        public bool Required;
        public string[] Hints = Array.Empty<string>();
    }

    private static readonly FieldDef[] Fields =
    {
        new() { Key = "code", Label = "代号（歌名，可留空）",
                Hints = new[] { "代号", "名称", "歌名", "标题", "name", "title", "code" } },
        new() { Key = "view", Label = "新增播放", Required = true,
                Hints = new[] { "播放", "观看", "view", "play" } },
        new() { Key = "favorite", Label = "新增收藏", Hints = new[] { "收藏", "fav" } },
        new() { Key = "like", Label = "新增点赞", Hints = new[] { "点赞", "赞", "like" } },
        new() { Key = "coin", Label = "新增投币", Hints = new[] { "投币", "币", "coin" } },
        new() { Key = "days", Label = "发布天数（留空=不折算）",
                Hints = new[] { "天数", "发布", "day", "days" } },
    };

    private readonly TableData _table;
    private readonly Dictionary<string, ComboBox> _combos = new();

    /// <summary>字段键 → 表格列索引（0 基），只包含用户选中的列</summary>
    public Dictionary<string, int> Mapping { get; } = new();

    public bool ClearExisting => ChkClear.IsChecked == true;

    // XAML 设计器需要无参构造
    public ImportTableWindow() : this(new TableData(), "")
    {
    }

    public ImportTableWindow(TableData table, string fileName)
    {
        InitializeComponent();

        _table = table;
        if (!string.IsNullOrEmpty(fileName)) _table.SourceName = fileName;

        BuildMappingRows();
        BuildPreview();
    }

    private void BuildMappingRows()
    {
        TxtFile.Text = string.IsNullOrWhiteSpace(_table.SourceName) ? "--" : _table.SourceName;
        TxtSummary.Text = $"识别到 {_table.ColumnCount} 列 · {_table.Rows.Count} 行数据" +
                          "　（第一行当作表头）";

        MapPanel.Children.Clear();
        var used = new HashSet<int>();

        foreach (var f in Fields)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("210,*"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            label.Children.Add(new TextBlock
            {
                Text = f.Label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (f.Required)
            {
                label.Children.Add(new TextBlock
                {
                    Text = "*",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse("#FFE81123")),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var items = new List<string> { "（不使用）" };
            for (int i = 0; i < _table.ColumnCount; i++)
                items.Add($"{ColumnName(i)} 列 · {_table.HeaderAt(i)}");

            var combo = new ComboBox
            {
                ItemsSource = items,
                SelectedIndex = GuessColumn(f, used) + 1, // +1：第 0 项是「（不使用）」
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0),
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            _combos[f.Key] = combo;
            MapPanel.Children.Add(row);
        }
    }

    /// <summary>按表头关键字猜列，猜不到返回 -1</summary>
    private int GuessColumn(FieldDef field, HashSet<int> used)    {
        for (int i = 0; i < _table.ColumnCount; i++)
        {
            if (used.Contains(i)) continue;

            var header = _table.HeaderAt(i);
            if (field.Hints.Any(h => header.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                used.Add(i);
                return i;
            }
        }

        return -1;
    }

    private void BuildPreview()    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("表头: " + string.Join(" | ", Enumerable.Range(0, _table.ColumnCount).Select(_table.HeaderAt)));

        int shown = 0;
        foreach (var r in _table.Rows)
        {
            if (shown >= 5) break;
            shown++;
            sb.AppendLine($"第{shown}行: " + string.Join(" | ", r.Select(v => string.IsNullOrWhiteSpace(v) ? "" : v)));
        }

        if (_table.Rows.Count > shown)
            sb.AppendLine($"…… 其余 {_table.Rows.Count - shown} 行略");

        TxtPreview.Text = sb.ToString();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Mapping.Clear();
        foreach (var f in Fields)
        {
            var index = _combos[f.Key].SelectedIndex;
            if (index > 0) Mapping[f.Key] = index - 1;
        }

        if (!Mapping.ContainsKey("view"))
        {
            _ = UiDialog.WarnAsync("还差一步", "「新增播放」必须指定表格里的一列，否则无法计分。");
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>0 基列号 → A、B … Z、AA、AB</summary>
    private static string ColumnName(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            int m = (index - 1) % 26;
            s = (char)('A' + m) + s;
            index = (index - 1) / 26;
        }
        return s;
    }
}
