using System.Collections.Concurrent;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace KRBWeekly.Controls;

/// <summary>
/// 网络封面图控件：URL 异步加载 + 内存缓存 + 同 URL 去重 + 失败灰底占位。
/// （Avalonia 不支持 WPF 的 Image.Source 绑定 URL 字符串自动转换，需自实现。）
/// </summary>
public partial class AsyncImage : UserControl
{
    // 静态缓存与并发请求表：多张卡片同一 URL 只下载一次
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> LoadTasks = new();
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static readonly StyledProperty<string?> SourceUrlProperty =
        AvaloniaProperty.Register<AsyncImage, string?>(nameof(SourceUrl));

    public string? SourceUrl
    {
        get => GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public AsyncImage()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceUrlProperty)
            _ = LoadAsync(change.GetNewValue<string?>());
    }

    private async Task LoadAsync(string? url)
    {
        Img.Source = null;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var bmp = await LoadTasks.GetOrAdd(url, FetchAsync);
            if (bmp != null && SourceUrl == url)
                Img.Source = bmp;
        }
        catch
        {
            // 加载失败保持灰底占位
        }
    }

    private static async Task<Bitmap?> FetchAsync(string url)
    {
        if (Cache.TryGetValue(url, out var cached)) return cached;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // B 站 CDN 对无 UA/Referer 的请求会拒图
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();

            var bmp = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(bytes);
                return new Bitmap(ms);
            });
            Cache[url] = bmp;
            return bmp;
        }
        catch
        {
            LoadTasks.TryRemove(url, out _);
            return null;
        }
        finally
        {
            LoadTasks.TryRemove(url, out _);
        }
    }
}
