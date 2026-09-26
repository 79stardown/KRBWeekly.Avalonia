using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace KRBWeekly.Helpers;

/// <summary>
/// Fluent 2 风格对话框统一封装（替代 WPF MessageBox）。
/// 注意：ContentDialog 是窗口内浮层、必须 await，所有调用点请用 async void + await。
/// </summary>
public static class UiDialog
{
    public static Task InfoAsync(string title, string message)
        => ShowAsync(title, message, "确定", null, ContentDialogButton.Primary);

    public static Task WarnAsync(string title, string message)
        => ShowAsync(title, message, "确定", null, ContentDialogButton.Primary);

    public static Task ErrorAsync(string title, string message)
        => ShowAsync(title, message, "确定", null, ContentDialogButton.Primary);

    /// <summary>确认框：返回 true = 点了主按钮（默认「确定」）</summary>
    public static async Task<bool> ConfirmAsync(string title, string message,
        string primaryText = "确定", string closeText = "取消")
    {
        var result = await ShowAsync(title, message, primaryText, closeText, ContentDialogButton.Primary);
        return result == ContentDialogResult.Primary;
    }

    private static async Task<ContentDialogResult> ShowAsync(string title, string message,
        string? primaryText, string? closeText, ContentDialogButton defaultButton)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = defaultButton
        };
        return await dialog.ShowAsync();
    }
}
