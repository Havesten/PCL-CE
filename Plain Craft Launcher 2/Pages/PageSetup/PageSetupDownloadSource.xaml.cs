using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Cdn;
using PCL.Core.UI.Hints;

namespace PCL;

/// <summary>
///     设置页面：官方源优选。负责解析官方 CDN 的全部节点、测速、并锁定延迟最低的节点供下载使用。
/// </summary>
public partial class PageSetupDownloadSource : IPageView
{
    /// <summary>下拉框中「自动」项的标记。</summary>
    private const string AutoTag = "auto";

    /// <summary>下拉框中「双栈优选」合成项的标记。</summary>
    private const string DualTag = "dual";

    private readonly Dictionary<string, CdnHostLatency> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CdnHostSpeed> _speeds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _probeCts;
    private bool _isBusy;
    private bool _isBuildingRows;
    private bool _isLoadedOnce;
    private double _lastRatio;

    public PageSetupDownloadSource()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public void OnNavigatedTo(object? parameter)
    {
    }

    public void OnNavigatedFrom()
    {
        CancelProbe();
    }

    #region 生命周期

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        PanBack.ScrollToHome();
        if (!_isLoadedOnce)
        {
            _isLoadedOnce = true;
            CheckPin.Checked = Config.Download.CdnPinEnabled;
            LabStatus.Text = Lang.Text("Setup.DownloadSource.Idle");
        }

        RefreshRows();
        RefreshProgress(_lastRatio);
        LabProxyWarning.Visibility = IsProxyInEffect() ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     系统代理生效时，建连目标会变成代理地址，按域名锁定节点无从生效，需要明确告知用户。
    /// </summary>
    private static bool IsProxyInEffect()
    {
        try
        {
            var probe = new Uri("https://piston-meta.mojang.com/");
            var proxy = PCL.Core.IO.Net.Http.HttpProxyManager.Instance.GetProxy(probe);
            return proxy is not null && !proxy.Equals(probe);
        }
        catch
        {
            return false;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelProbe();
    }

    private void CancelProbe()
    {
        try
        {
            _probeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
    }

    #endregion

    #region 交互

    private void CheckPin_Change(object sender, bool user)
    {
        if (!user) return;

        if (CheckPin.Checked == true)
        {
            if (CdnNodePin.Snapshot().Count == 0)
            {
                CheckPin.Checked = false;
                HintService.Show(Hints.Info(Lang.Text("Setup.DownloadSource.Pin.Empty"), log: false));
                return;
            }

            CdnNodePin.SetEnabled(true);
        }
        else
        {
            CdnNodePin.SetEnabled(false);
        }
    }

    private async void BtnProbe_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy) return;

        SetBusy(true);
        RefreshProgress(0d);

        var progress = new Progress<CdnProbeProgress>(OnProbeProgress);
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;

        try
        {
            var results = await OfficialCdnOptimizer.ProbeAsync(progress, token);

            _results.Clear();
            // 延迟结果变了，此前基于旧节点测出的速率作废
            _speeds.Clear();
            foreach (var result in results)
                _results[result.Host] = result;

            var applied = OfficialCdnOptimizer.ApplyBestNodes(results);
            CheckPin.Checked = Config.Download.CdnPinEnabled;
            LabStatus.Text = applied > 0
                ? Lang.Text("Setup.DownloadSource.Done", applied)
                : Lang.Text("Setup.DownloadSource.NoNode");

            RefreshRows();
            if (applied > 0)
                HintService.Show(Hints.Success(Lang.Text("Setup.DownloadSource.Done", applied)));
        }
        catch (OperationCanceledException)
        {
            LabStatus.Text = Lang.Text("Setup.DownloadSource.Cancelled");
        }
        catch (Exception ex)
        {
            Common.Log(ex, "官方源优选失败", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.DownloadSource.Failed"));
            LabStatus.Text = Lang.Text("Setup.DownloadSource.Failed");
            HintService.Show(Hints.Error(Lang.Text("Setup.DownloadSource.Failed")));
        }
        finally
        {
            SetBusy(false);
            _probeCts?.Dispose();
            _probeCts = null;
            RefreshProgress(_lastRatio);
        }
    }

    /// <summary>
    ///     节点测速：对延迟最低的前若干个节点按顺序逐个测速，最终按平均速率选出最优节点。
    /// </summary>
    private async void BtnSpeedTest_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy) return;

        if (_results.Count == 0)
        {
            HintService.Show(Hints.Info(Lang.Text("Setup.DownloadSource.Speed.NeedLatency"), log: false));
            return;
        }

        SetBusy(true);
        RefreshProgress(0d);

        var progress = new Progress<CdnSpeedProgress>(OnSpeedProgress);
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;

        try
        {
            var speeds = await OfficialCdnSpeedTester.ProbeAsync(_results.Values.ToArray(), progress, token);

            _speeds.Clear();
            foreach (var speed in speeds)
                _speeds[speed.Host] = speed;

            var applied = OfficialCdnSpeedTester.ApplyBestBySpeed(speeds);
            CheckPin.Checked = Config.Download.CdnPinEnabled;
            LabStatus.Text = applied > 0
                ? Lang.Text("Setup.DownloadSource.Speed.Done", applied)
                : Lang.Text("Setup.DownloadSource.Speed.NoResult");

            RefreshRows();
            if (applied > 0)
                HintService.Show(Hints.Success(Lang.Text("Setup.DownloadSource.Speed.Done", applied)));
        }
        catch (OperationCanceledException)
        {
            LabStatus.Text = Lang.Text("Setup.DownloadSource.Cancelled");
        }
        catch (Exception ex)
        {
            Common.Log(ex, "节点测速失败", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.DownloadSource.Speed.Failed"));
            LabStatus.Text = Lang.Text("Setup.DownloadSource.Speed.Failed");
            HintService.Show(Hints.Error(Lang.Text("Setup.DownloadSource.Speed.Failed")));
        }
        finally
        {
            SetBusy(false);
            _probeCts?.Dispose();
            _probeCts = null;
            RefreshProgress(_lastRatio);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BtnProbe.IsEnabled = !busy;
        BtnSpeedTest.IsEnabled = !busy;
        BtnClear.IsEnabled = !busy;
    }

    private void OnSpeedProgress(CdnSpeedProgress progress)
    {
        RefreshProgress(progress.Ratio);
        LabStatus.Text = Lang.Text(
            "Setup.DownloadSource.Speed.Progress",
            $"{progress.Host} · {progress.Node}",
            progress.Sample,
            progress.Samples,
            progress.MebibytesPerSecond.ToString("F1"));
    }

    private void BtnClear_Click(object sender, MouseButtonEventArgs e)
    {
        CancelProbe();
        _results.Clear();
        _speeds.Clear();
        CdnNodePin.Clear();
        CheckPin.Checked = false;
        LabStatus.Text = Lang.Text("Setup.DownloadSource.Cleared");
        RefreshProgress(0d);
        RefreshRows();
    }

    private void OnProbeProgress(CdnProbeProgress progress)
    {
        RefreshProgress(progress.Ratio);
        LabStatus.Text = Lang.Text(
            "Setup.DownloadSource.Probing",
            progress.Completed,
            progress.Total,
            progress.Round,
            progress.Rounds);
    }

    private void BarTrack_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshProgress(_lastRatio);

    private void RefreshProgress(double ratio)
    {
        _lastRatio = Math.Clamp(ratio, 0d, 1d);
        BarValue.Width = Math.Max(0d, BarTrack.ActualWidth * _lastRatio);
    }

    #endregion

    #region 节点列表

    private void RefreshRows()
    {
        PanNodes.Children.Clear();
        _isBuildingRows = true;
        try
        {
            var pins = CdnNodePin.Snapshot();
            foreach (var group in OfficialCdnOptimizer.Groups)
            {
                PanNodes.Children.Add(BuildGroupHeader(Lang.Text(group.TitleKey)));
                foreach (var host in group.Hosts)
                {
                    _results.TryGetValue(host, out var result);
                    PanNodes.Children.Add(BuildHostRow(host, result, pins));
                }
            }
        }
        finally
        {
            _isBuildingRows = false;
        }
    }

    /// <summary>
    ///     分组标题，用于区分 Mojang、模组加载器与各类模组源的域名。
    /// </summary>
    private static TextBlock BuildGroupHeader(string title) => new()
    {
        Text = title,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 8),
        Opacity = 0.85
    };

    private Grid BuildHostRow(
        string host,
        CdnHostLatency? result,
        IReadOnlyDictionary<string, IReadOnlyList<System.Net.IPAddress>> pins)
    {
        var row = new Grid { Height = 32, Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

        var label = new TextBlock
        {
            Text = host,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.85
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var combo = BuildNodeCombo(host, result, pins);
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);

        // 右侧展示 v4 / v6 两条路径各自的最优值（测过速时以速率为准）
        _speeds.TryGetValue(host, out var speed);
        var detail = DescribeBest(result, speed);
        // Cloudflare 站点用的是优选 IP 列表，与自己的 DNS 结果无关，需要明显标出来
        if (result is { FromPreferredPool: true })
            detail = detail.Length > 0
                ? $"{Lang.Text("Setup.DownloadSource.Node.Preferred")}  ·  {detail}"
                : Lang.Text("Setup.DownloadSource.Node.Preferred");

        var summary = new TextBlock
        {
            Text = detail,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6
        };
        Grid.SetColumn(summary, 2);
        row.Children.Add(summary);

        return row;
    }

    /// <summary>
    ///     把 v4 / v6 两条路径的最优结果拼成一行摘要，测过速就用速率，否则用延迟。
    /// </summary>
    private static string DescribeBest(CdnHostLatency? result, CdnHostSpeed? speed)
    {
        if (speed is not null)
        {
            var text = "";
            if (speed.BestIPv4 is { } bestV4) text = $"v4 {bestV4.MebibytesPerSecond:F1}";
            if (speed.BestIPv6 is { } bestV6)
                text += $"{(text.Length > 0 ? "  ·  " : "")}v6 {bestV6.MebibytesPerSecond:F1}";
            if (text.Length > 0) return text + " MiB/s";
        }

        var latency = "";
        if (result?.BestIPv4 is { } lowV4) latency = $"v4 {lowV4.MedianMs:F0} ms";
        if (result?.BestIPv6 is { } lowV6)
            latency += $"{(latency.Length > 0 ? "  ·  " : "")}v6 {lowV6.MedianMs:F0} ms";
        if (latency.Length > 0) return latency;

        return result is { Nodes.Count: > 0 } ? Lang.Text("Setup.DownloadSource.Node.Timeout") : "";
    }

    private MyComboBox BuildNodeCombo(
        string host,
        CdnHostLatency? result,
        IReadOnlyDictionary<string, IReadOnlyList<System.Net.IPAddress>> pins)
    {
        var combo = new MyComboBox
        {
            Height = 28,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = host
        };

        combo.Items.Add(new MyComboBoxItem
        {
            Content = Lang.Text("Setup.DownloadSource.Node.Auto"),
            Tag = AutoTag
        });

        pins.TryGetValue(host, out var pinnedAddresses);
        _speeds.TryGetValue(host, out var speed);
        var selectedIndex = 0;

        // 双栈优选会同时锁定 v4 与 v6 各一个节点，下拉框用一个合成项表示这种状态
        if (pinnedAddresses is { Count: > 1 })
        {
            combo.Items.Add(new MyComboBoxItem
            {
                Content = Lang.Text("Setup.DownloadSource.Node.Dual",
                    DescribePinned(pinnedAddresses, speed, ipv6: false),
                    DescribePinned(pinnedAddresses, speed, ipv6: true)),
                Tag = DualTag
            });
            selectedIndex = 1;
        }

        // 先列 IPv4 再列 IPv6，各自按延迟升序，避免两族混排时看不清
        foreach (var node in (result?.IPv4Nodes ?? Array.Empty<CdnNodeLatency>())
                     .Concat(result?.IPv6Nodes ?? Array.Empty<CdnNodeLatency>()))
        {
            var speedNode = speed?.Nodes.FirstOrDefault(item =>
                item.IsReachable && item.Address.Equals(node.Address));
            var rateText = speedNode is null ? "" : $"  ·  {speedNode.MebibytesPerSecond:F1} MiB/s";

            var text = node.IsReachable
                ? $"{node.FamilyLabel}  ·  {node.Address}  ·  {node.MedianMs:F0} ms{rateText}  ({node.SuccessCount}/{node.TotalCount})"
                : $"{node.FamilyLabel}  ·  {node.Address}  ·  {Lang.Text("Setup.DownloadSource.Node.Timeout")}";

            combo.Items.Add(new MyComboBoxItem { Content = text, Tag = node.Address });

            if (pinnedAddresses is { Count: 1 } && pinnedAddresses[0].Equals(node.Address))
                selectedIndex = combo.Items.Count - 1;
        }

        // 尚未测速但已锁定单个节点时，至少把它显示出来（双栈情况上面的合成项已经足够）
        if (result is null && pinnedAddresses is { Count: 1 })
        {
            combo.Items.Add(new MyComboBoxItem { Content = pinnedAddresses[0].ToString(), Tag = pinnedAddresses[0] });
            selectedIndex = combo.Items.Count - 1;
        }

        combo.SelectedIndex = selectedIndex;
        combo.SelectionChanged += NodeCombo_SelectionChanged;
        return combo;
    }

    /// <summary>
    ///     描述某个地址族当前锁定的节点，附带速率（如果测过）。
    /// </summary>
    private static string DescribePinned(
        IReadOnlyList<System.Net.IPAddress> addresses,
        CdnHostSpeed? speed,
        bool ipv6)
    {
        var family = ipv6
            ? System.Net.Sockets.AddressFamily.InterNetworkV6
            : System.Net.Sockets.AddressFamily.InterNetwork;
        var address = addresses.FirstOrDefault(item => item.AddressFamily == family);
        if (address is null) return "-";

        var rate = speed?.Nodes.FirstOrDefault(item => item.IsReachable && item.Address.Equals(address));
        return rate is null ? address.ToString() : $"{address} {rate.MebibytesPerSecond:F1} MiB/s";
    }

    private void NodeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBuildingRows || sender is not MyComboBox combo) return;
        if (combo.Tag is not string host) return;
        if (combo.SelectedItem is not MyComboBoxItem item) return;

        switch (item.Tag)
        {
            // 「自动」：解除该域名的锁定，回到系统 DNS 解析
            case AutoTag:
                CdnNodePin.RemovePin(host);
                return;
            // 「双栈优选」是已有状态的展示项，本身不改变锁定
            case DualTag:
                return;
        }

        if (item.Tag is not System.Net.IPAddress address) return;

        CdnNodePin.SetPin(host, address);
        if (!Config.Download.CdnPinEnabled)
        {
            Config.Download.CdnPinEnabled = true;
            CheckPin.Checked = true;
        }
    }

    #endregion
}
