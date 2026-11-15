using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;

namespace PCL;

public class MyImage : Image
{
    // 资源列表一屏就是几十上百个图标。不限制并发的话，一次全部发起下载 + 解码，
    // 网络栈和 CPU 会被同时打满，表现就是「图标一多，搜索要等半天才回来」。
    // 下载与解码各开一道闸，多余的排队，不影响单个图片的加载逻辑。
    private const int MaxConcurrentLoads = 6;

    private static readonly SemaphoreSlim DownloadSlots = new(MaxConcurrentLoads);

    private static readonly SemaphoreSlim DecodeSlots = new(MaxConcurrentLoads);

    // 同一个图片文件经常被重复显示（重建列表、滚动回来），
    // 解码结果按「路径 + 修改时间」缓存，避免每次重新解码并泄漏 GDI+ 位图。
    private static readonly ConcurrentDictionary<string, ImageSource> DecodedCache = new();

    private const int DecodedCacheLimit = 512;

    public MyImage()
    {
        Initialized += (_, _) => Load();
        SizeChanged += (_, _) => UpdateClip();
    }

    /// <summary>
    ///     实际被呈现的图片地址。
    /// </summary>
    public string ActualSource
    {
        get => field;
        set
        {
            if (string.IsNullOrEmpty(value))
                value = null;
            if ((field ?? "") == (value ?? ""))
                return; // 属性读取顺序修正：在完成 XAML 属性读取后再触发图片加载（#4868）
            field = value;
            Dispatcher.BeginInvoke(new Func<Task>(async () =>
            {
                try
                {
                    if (value is null)
                    {
                        base.Source = null;
                        return;
                    }

                    var cacheKey = GetDecodeCacheKey(value);
                    if (DecodedCache.TryGetValue(cacheKey, out var cached))
                    {
                        base.Source = cached;
                        return;
                    }

                    ImageSource bitmap;
                    await DecodeSlots.WaitAsync();
                    try
                    {
                        bitmap = await Task.Run(() => new MyBitmap(value));
                    }
                    finally
                    {
                        DecodeSlots.Release();
                    }

                    if (bitmap is not null)
                    {
                        if (DecodedCache.Count >= DecodedCacheLimit)
                            DecodedCache.Clear(); // 简单粗暴地上限保护，不做 LRU
                        DecodedCache[cacheKey] = bitmap;
                    }

                    base.Source = bitmap;
                }
                catch (Exception ex)
                {
                    Common.Log(ex, $"加载图片失败（{value}）");
                    try
                    {
                        if (value.StartsWithF(Common.pathTemp) && File.Exists(value))
                            File.Delete(value);
                    }
                    catch
                    {
                    }
                }
            })); // 在这里先触发可能的文件读取，尽量避免在 UI 线程中读取文件
            // ignored
        }
    }

    /// <summary>
    ///     解码缓存的键：文件路径 + 最后写入时间；临时缓存图被重下时应重新解码。
    /// </summary>
    private static string GetDecodeCacheKey(string path)
    {
        try
        {
            if (File.Exists(path))
                return path + "|" + new FileInfo(path).LastWriteTimeUtc.Ticks;
        }
        catch
        {
            // 拿不到时间就当不能缓存
        }

        return path + "|0";
    }

    private void Load() // 属性读取顺序修正：在完成 XAML 属性读取后再触发图片加载（#4868）
    {
        if (Source is null)
        {
            ActualSource = null;
            return;
        }

        if (!Source.StartsWithF("http"))
        {
            ActualSource = Source;
            return;
        }

        var url = Source;
        var tempPath = GetTempPath(url);
        var tempFile = new FileInfo(tempPath);
        var enableCache = this.EnableCache;
        if (enableCache && tempFile.Exists)
        {
            ActualSource = tempPath;
            if (DateTime.Now - tempFile.LastWriteTime < fileCacheExpiredTime)
                return; // 无需刷新缓存
        }

        Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            try
            {
                // 下载
                ActualSource = LoadingSource;

                var resp = await DownloadImageAsync(url);
                if (!string.IsNullOrEmpty(resp))
                {
                    ActualSource = resp;
                    return;
                }

                resp = await DownloadImageAsync(FallbackSource);
                if (!string.IsNullOrEmpty(resp))
                {
                    ActualSource = resp;
                    return;
                }
            }
            catch (Exception ex)
            {
                // 更换备用地址
                Common.Log(ex,
                    $"Online image get fail（source = {url}, fallback = {FallbackSource}）",
                    Common.LogLevel.Developer);
                tempPath = GetTempPath(url);
                tempFile = new FileInfo(tempPath);
                if (enableCache && tempFile.Exists)
                {
                    ActualSource = tempPath;
                    if (DateTime.Now - tempFile.LastWriteTime < fileCacheExpiredTime)
                        return;
                }
            }
        }));
    }

    public static Task<string> DownloadImageAsync(string url)
    {
        return DownloadTasks.GetOrAdd(url, key =>
        {
            var t = DownloadImageInternalAsync(key);
            t.ContinueWith(_ => DownloadTasks.TryRemove(url, out _));
            return t;
        });
    }

    public static string GetTempPath(string url)
    {
        return Path.Combine(Common.pathTemp, "Cache", "Images", $"{Common.GetStringMd5(url)}.png");
    }

    private static readonly ConcurrentDictionary<string, Task<string>> DownloadTasks = new();

    private static async Task<string> DownloadImageInternalAsync(string url)
    {
        var tempPath = GetTempPath(url);
        var tempDownloadingPath = tempPath + RandomUtils.NextInt(0, 1000000);

        // 与解码分开的两道闸：下载排队，避免几十上百个图标同时抢占连接
        await DownloadSlots.WaitAsync();
        try
        {
            Directory.CreateDirectory(
                Common.GetPathFromFullPath(tempPath)); // 重新实现下载，以避免携带 Header
            using (var fs = new FileStream(tempDownloadingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                using (var response = await HttpRequest.Create(url)
                           .SendAsync(addMetedata: false))
                {
                    response.EnsureSuccessStatusCode();

                    using (var nfs = await response.AsStreamAsync())
                    {
                        fs.SetLength(0L);
                        await nfs.CopyToAsync(fs);
                    }
                }
            }

            File.Move(tempDownloadingPath, tempPath, true);
            return tempPath;
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(tempDownloadingPath)) File.Delete(tempDownloadingPath);

            Common.Log(ex, $"Try to get online image fail (url = {url}, dest = {tempPath})");
            return string.Empty;
        }
        finally
        {
            DownloadSlots.Release();
        }
    }

    #region 公开属性

    public TimeSpan fileCacheExpiredTime = TimeSpan.FromDays(14d);

    public bool EnableCache
    {
        get => (bool)GetValue(EnableCacheProperty);
        set => SetValue(EnableCacheProperty, value);
    }

    public new static readonly DependencyProperty EnableCacheProperty =
        DependencyProperty.Register("EnableCache", typeof(bool), typeof(MyImage), new PropertyMetadata(true));

    /// <summary>
    ///     与 Image 的 Source 类似。
    ///     若输入以 http 开头的字符串，则会尝试下载图片然后显示，图片会保存为本地缓存。
    ///     支持 WebP 格式的图片。
    /// </summary>
    public new string? Source // 覆写 Image 的 Source 属性
    {
        get => field;
        set
        {
            if (string.IsNullOrEmpty(value))
                value = null;
            if ((field ?? "") == (value ?? ""))
                return;
            field = value;
            if (!IsInitialized)
                return; // 属性读取顺序修正：在完成 XAML 属性读取后再触发图片加载（#4868）
            Load();
        }
    } = "";

    public new static readonly DependencyProperty SourceProperty = DependencyProperty.Register("Source", typeof(string),
        typeof(MyImage), new PropertyMetadata((sender, e) =>
        {
            if (sender is not null) ((MyImage)sender).Source = e.NewValue.ToString();
        }));

    /// <summary>
    ///     当 Source 首次下载失败时，会从该备用地址加载图片。
    /// </summary>
    public string FallbackSource { get; set; }

    /// <summary>
    ///     正在下载网络图片时显示的本地图片。
    /// </summary>
    public string LoadingSource { get; set; } = "pack://application:,,,/images/Icons/NoIcon.png";
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
    private static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            "CornerRadius",
            typeof(CornerRadius),
            typeof(MyImage),
            new FrameworkPropertyMetadata(
                new CornerRadius(-1),
                OnCornerRadiusChanged)
        );

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MyImage)d).UpdateClip();
    }

    private void UpdateClip() // Handles Me.SizeChanged will be added separately
    {
        if (ActualWidth > 0 && ActualHeight > 0 &&
            CornerRadius.TopLeft >= 0 && CornerRadius.TopRight >= 0)
        {
            Clip = new RectangleGeometry(
                new Rect(0, 0, ActualWidth, ActualHeight),
                CornerRadius.TopLeft,
                CornerRadius.TopRight);
        }
    }
    #endregion
}
