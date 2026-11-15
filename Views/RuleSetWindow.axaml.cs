using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KRBWeekly.Helpers;
using KRBWeekly.Models;
using KRBWeekly.Services;

namespace KRBWeekly.Views;

/// <summary>
/// 独立的规则集编辑窗口：设置页只放一个摘要 + 「修改规则集」入口，
/// 编辑器（条件 → 规则组 → 规则集）挪到这里，避免设置页被撑得太长。
/// </summary>
public partial class RuleSetWindow : Window
{
    private readonly string _dataDir = "";
    private readonly ConfigService _configSvc;

    public ObservableCollection<RuleGroupVm> RuleGroups { get; } = new();

    /// <summary>保存/恢复后触发，调用方据此刷新摘要</summary>
    public event Action? RulesChanged;

    // XAML 设计器需要无参构造
    public RuleSetWindow()
    {
        InitializeComponent();
        _configSvc = new ConfigService(Path.Combine(AppContext.BaseDirectory, "Data"));
        RuleGroupList.ItemsSource = RuleGroups;
    }

    public RuleSetWindow(string dataDir) : this()
    {
        _dataDir = dataDir;
        _configSvc = new ConfigService(dataDir);
        LoadRules(_configSvc.Load());
    }

    // ===== 规则编辑 =====

    /// <summary>组间关系：true = 全部满足（All），false = 任一满足（Any）</summary>
    private bool _matchAll;

    private void LoadRules(ScoringConfig c)
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
        RulesChanged?.Invoke();

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
        RulesChanged?.Invoke();

        LoadRules(c);
        await UiDialog.InfoAsync("完成", "已恢复默认筛选规则，下次统计生效。");
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}