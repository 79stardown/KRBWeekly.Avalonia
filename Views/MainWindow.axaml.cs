using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace KRBWeekly.Views;

public partial class MainWindow : Window
{
    // 缓存视图实例，避免导航切换时销毁重建导致统计中断（沿用旧版意图）
    private readonly StatisticsView _statsView = new();
    private readonly FilterView _filterView = new();
    private readonly ManualEntryView _manualView = new();
    private readonly SettingsView _settingsView = new();

    public MainWindow()
    {
        InitializeComponent();

        _statsView.StatisticsCompleted += OnStatisticsCompleted;
        _filterView.BackRequested += () => ContentHost.Content = _statsView;

        // 初始选中第一项（自动计算）
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentHost.Content = _statsView;
    }

    /// <summary>统计完成自动存档后：切到筛选视图并选中最新记录</summary>
    private void OnStatisticsCompleted()
    {
        ContentHost.Content = _filterView;
        _filterView.SelectNewestRecord();
    }

    private void NavView_SelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        var item = e.SelectedItem as NavigationViewItem;
        ContentHost.Content = item?.Tag switch
        {
            "PageAuto" => (Control)_statsView,
            "PageManual" => (Control)_manualView,
            "PageSettings" => (Control)_settingsView,
            _ => ContentHost.Content
        };
    }
}
