using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using KRBWeekly.Models;

namespace KRBWeekly.Views;

/// <summary>
/// 设置页「筛选规则」编辑器的一条条件。
/// 只做「界面 ↔ 模型」的搬运：数值先用字符串存，点保存时才解析校验，
/// 这样用户半途输入（空串、小数点）不会让绑定把控件值吞掉。
/// </summary>
public class RuleVm : INotifyPropertyChanged
{
    /// <summary>下拉项：字段名（顺序与 <see cref="FilterField.All"/> 一致）</summary>
    public static IReadOnlyList<string> FieldChoices { get; } =
        FilterField.All.Select(x => x.Label).ToList();

    /// <summary>下拉项：比较方式（顺序与 <see cref="FilterOp.All"/> 一致）</summary>
    public static IReadOnlyList<string> OpChoices { get; } =
        FilterOp.All.Select(x => x.Label).ToList();

    private int _fieldIndex = FilterField.IndexOf(FilterField.Days);
    private int _opIndex = FilterOp.IndexOf(FilterOp.Ge);
    private bool _isReversed;
    private string _minText = "";
    private string _maxText = "";

    public int FieldIndex
    {
        get => _fieldIndex;
        set { if (_fieldIndex != value) { _fieldIndex = value; OnPropertyChanged(); } }
    }

    public int OpIndex
    {
        get => _opIndex;
        set
        {
            if (_opIndex == value) return;
            _opIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRange));
        }
    }

    /// <summary>区间比较才显示「至 上界」两个控件</summary>
    public bool IsRange => _opIndex == FilterOp.IndexBetween;

    /// <summary>「非」：条件成立时反而判为不满足</summary>
    public bool IsReversed
    {
        get => _isReversed;
        set { if (_isReversed != value) { _isReversed = value; OnPropertyChanged(); } }
    }

    public string MinText
    {
        get => _minText;
        set { if (_minText != value) { _minText = value; OnPropertyChanged(); } }
    }

    public string MaxText
    {
        get => _maxText;
        set { if (_maxText != value) { _maxText = value; OnPropertyChanged(); } }
    }

    public static RuleVm FromModel(FilterRule r) => new()
    {
        FieldIndex = FilterField.IndexOf(r.Field),
        OpIndex = FilterOp.IndexOf(r.Op),
        IsReversed = r.IsReversed,
        MinText = r.Min.ToString("0.###", CultureInfo.InvariantCulture),
        MaxText = r.Max.ToString("0.###", CultureInfo.InvariantCulture),
    };

    public RuleVm Clone() => new()
    {
        FieldIndex = FieldIndex,
        OpIndex = OpIndex,
        IsReversed = IsReversed,
        MinText = MinText,
        MaxText = MaxText,
    };

    /// <summary>解析成模型；数值非法或区间倒置时返回 false 并给出原因</summary>
    public bool TryToModel(out FilterRule rule, out string error)
    {
        rule = new FilterRule
        {
            Field = FilterField.CodeAt(FieldIndex),
            Op = FilterOp.CodeAt(OpIndex),
            IsReversed = IsReversed,
        };
        error = "";

        if (!double.TryParse(MinText?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var min) &&
            !double.TryParse(MinText?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out min))
        {
            error = "数值未填写或不是合法数字";
            return false;
        }

        rule.Min = min;

        if (rule.Op == FilterOp.Between)
        {
            if (!double.TryParse(MaxText?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var max) &&
                !double.TryParse(MaxText?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out max))
            {
                error = "区间的上界未填写或不是合法数字";
                return false;
            }
            if (max < min)
            {
                error = "区间的上界小于下界";
                return false;
            }
            rule.Max = max;
        }
        else
        {
            rule.Max = min;
        }

        // 只有「发布天数」允许 0（当天发布）；量级字段负数没有意义
        if (rule.Field != FilterField.Days && rule.Min < 0)
        {
            error = "该字段不能为负数";
            return false;
        }

        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>设置页「筛选规则」编辑器的一个规则组</summary>
public class RuleGroupVm : INotifyPropertyChanged
{
    private string _title = "";
    private bool _enabled = true;
    private int _matchModeIndex = 1; // 0 = 任一，1 = 全部

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
    }

    /// <summary>0 = 组内任一满足；1 = 组内全部满足</summary>
    public int MatchModeIndex
    {
        get => _matchModeIndex;
        set
        {
            if (_matchModeIndex == value) return;
            _matchModeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMode));
            OnPropertyChanged(nameof(IsAllMode));
        }
    }

    // 两个 ToggleButton 各自绑一个：点哪个就切到哪个，再点已选中的不会取消（回弹）
    public bool IsAnyMode
    {
        get => _matchModeIndex == 0;
        set
        {
            if (value) MatchModeIndex = 0;
            else OnPropertyChanged(); // 不允许把当前选中项点掉
        }
    }

    public bool IsAllMode
    {
        get => _matchModeIndex == 1;
        set
        {
            if (value) MatchModeIndex = 1;
            else OnPropertyChanged();
        }
    }

    public ObservableCollection<RuleVm> Rules { get; } = new();

    public static RuleGroupVm FromModel(FilterRuleGroup g)
    {
        var vm = new RuleGroupVm
        {
            Enabled = g.Enabled,
            MatchModeIndex = string.Equals(g.MatchMode, FilterMatch.Any, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
        };
        foreach (var r in g.Rules) vm.Rules.Add(RuleVm.FromModel(r));
        return vm;
    }

    /// <summary>复制本组（条件一起复制，标题由 RenumberGroups 重排）</summary>
    public RuleGroupVm Clone()
    {
        var vm = new RuleGroupVm { Enabled = Enabled, MatchModeIndex = MatchModeIndex };
        foreach (var r in Rules) vm.Rules.Add(r.Clone());
        return vm;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
