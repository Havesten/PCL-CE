using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Link.Relay;

namespace PCL;

/// <summary>
///     中继联机面板，内嵌在联机页的第三级子页面里。
///     配额、限速、端口数、封禁状态全部由服务端裁决，本地只做展示。
/// </summary>
public partial class RelayPanel : UserControl
{
    private readonly RelaySession _session = RelaySession.Load();
    private readonly RelayApi _api;
    private readonly DispatcherTimer _timer;
    private RelayCaptcha? _captcha;
    private TunnelClient? _runner;
    private string? _runningAllocationId;
    private bool _initialized;
    private bool _banned;
    private bool _hasNodes;
    private bool _busy;
    private bool _versionTooLow;

    public RelayPanel()
    {
        InitializeComponent();
        _api = RelaySession.CreateApi(_session);
        // 这个面板常驻在联机页里，只有真正切到这个子页面时才算可见，
        // 所以初始化挂在可见性变化上，避免启动时就发请求。
        IsVisibleChanged += OnVisibleChanged;
        Application.Current.Exit += (_, _) => OnAppExit();

        // 停留在页面上时定期刷新：管理员改配额、封号都是即时生效的，
        // 不能等到用户下次切页才看到。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) =>
        {
            if (!IsVisible || !_session.IsValid || _busy) return;
            await RefreshAsync().ConfigureAwait(true);
        };
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();
        try
        {
            if (!_initialized)
            {
                _initialized = true;
                await InitializeAsync().ConfigureAwait(true);
                return;
            }

            // 再次进来时刷新一下配额，端口可能已经在别处被释放了。
            if (_session.IsValid) await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 任何没预料到的异常都只落到状态栏，不能让整页变成空白。
            SetStatus("初始化失败：" + ex.Message);
            SetBusy(false);
        }
    }

    private async Task InitializeAsync()
    {
        await CheckVersionAsync().ConfigureAwait(true);
        if (_session.IsValid)
        {
            ShowLoggedIn();
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        // 本地没有可用令牌：曾经登录过的会话才算「已过期」，否则就是首次使用
        ShowLogin(_session.ExpiresAt != default);
        await RefreshCaptchaAsync().ConfigureAwait(true);
    }

    /// <summary>
    ///     版本门槛预检：云端后台设了「联机客户端版本号」时，客户端版本必须完全一致，
    ///     不一致（无论高低）都提示版本过低、需要更新，并禁用分配按钮。
    ///     拿不到状态不拦功能——真正分配时服务端还会再校验一次。
    /// </summary>
    private async Task CheckVersionAsync()
    {
        try
        {
            var status = await _api.GetStatusAsync().ConfigureAwait(true);
            var required = status.RequiredVersion?.Trim();
            _versionTooLow = !string.IsNullOrEmpty(required) && required != Basics.VersionName;
            if (_versionTooLow) SetStatus("版本过低，需要更新");
        }
        catch (Exception)
        {
            // 状态接口失败不改变既有判定，分配时服务端兜底
        }
        finally
        {
            UpdateButtons();
        }
    }

    // ---------------------------------------------------------------- 登录

    private async Task RefreshCaptchaAsync()
    {
        try
        {
            SetBusy(true, "正在获取验证码…");
            _captcha = await _api.GetCaptchaAsync().ConfigureAwait(true);
            LabCaptcha.Text = _captcha.Prompt;
            TextCaptcha.Text = string.Empty;
            SetStatus("请输入算式答案后发送验证码");
        }
        catch (Exception ex)
        {
            SetStatus("获取人机验证失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnRefreshCaptcha_Click(object sender, MouseButtonEventArgs e) =>
        await RefreshCaptchaAsync().ConfigureAwait(true);

    private async void BtnSendCode_Click(object sender, MouseButtonEventArgs e)
    {
        var email = TextEmail.Text.Trim();
        if (email.Length == 0)
        {
            SetStatus("请先填写邮箱");
            return;
        }

        if (_captcha is null)
        {
            await RefreshCaptchaAsync().ConfigureAwait(true);
            return;
        }

        if (TextCaptcha.Text.Trim().Length == 0)
        {
            SetStatus("请填写人机验证的答案");
            return;
        }

        try
        {
            SetBusy(true, "正在发送…");
            var devCode = await _api
                .SendCodeAsync(email, _captcha.Id, TextCaptcha.Text.Trim())
                .ConfigureAwait(true);

            LabCodeHint.Text = devCode is null
                ? "已发送，请查收邮件（10 分钟内有效）"
                : $"开发模式验证码：{devCode}";
            if (devCode is not null) TextCode.Text = devCode;
            SetStatus("验证码已发送");
            _captcha = null;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            await RefreshCaptchaAsync().ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnLogin_Click(object sender, MouseButtonEventArgs e)
    {
        var email = TextEmail.Text.Trim();
        var code = TextCode.Text.Trim();
        if (email.Length == 0 || code.Length == 0)
        {
            SetStatus("请填写邮箱与验证码");
            return;
        }

        try
        {
            SetBusy(true, "正在登录…");
            var result = await _api.LoginAsync(email, code).ConfigureAwait(true);

            _session.Token = result.Token;
            _session.Email = result.User.Email;
            _session.ExpiresAt = result.ExpiresAt;
            _session.Save();
            _api.Token = result.Token;

            ShowLoggedIn();
            ApplyQuota(result.Quota);
            SetStatus(result.IsNew ? "注册成功，已自动创建账号" : "登录成功");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus("登录失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnLogout_Click(object sender, MouseButtonEventArgs e)
    {
        _session.Clear();
        _api.Token = null;
        ListPorts.ItemsSource = null;
        LabEmpty.Visibility = Visibility.Visible;
        ShowLogin();
        // 退出后人机验证仍是上一次登录用掉的那道，得重新取一道才能发码。
        await RefreshCaptchaAsync().ConfigureAwait(true);
        SetStatus("已退出登录");
    }

    // ---------------------------------------------------------------- 数据

    private async Task RefreshAsync()
    {
        if (!_session.IsValid) return;
        try
        {
            await CheckVersionAsync().ConfigureAwait(true);
            var me = await _api.GetMeAsync().ConfigureAwait(true);
            ApplyMe(me);
            ApplyQuota(me.Quota);
            ApplyAllocations(me.Allocations);
            await LoadNodesAsync().ConfigureAwait(true);
        }
        catch (RelayApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            _session.Clear();
            _api.Token = null;
            ShowLogin(true);
            await RefreshCaptchaAsync().ConfigureAwait(true);
            SetStatus("登录已过期，请重新登录");
        }
        catch (Exception ex)
        {
            SetStatus("刷新失败：" + ex.Message);
        }
    }

    /// <summary>把账号状态（邮箱、用户组、封禁）刷到界面上。</summary>
    private void ApplyMe(RelayMe me)
    {
        _banned = me.IsBanned;
        SetAuthState(_banned ? AuthState.Banned : AuthState.LoggedIn, me);
        if (_banned) SetStatus("账号已被封禁");
        UpdateButtons();
    }

    private async Task LoadNodesAsync()
    {
        IReadOnlyList<RelayNode> nodes;
        try
        {
            nodes = await _api.GetNodesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 拉不到节点时不能把下拉框留成空白，否则看起来像是页面没渲染出来。
            nodes = [];
            SetStatus("获取中继节点失败：" + ex.Message);
        }

        var selected = (ComboNodes.SelectedItem as RelayNode)?.Id;
        if (nodes.Count == 0)
        {
            ComboNodes.ItemsSource = new[] { new RelayNode("", "暂无可用节点", "", "", 0, 0) };
            ComboNodes.SelectedIndex = 0;
            _hasNodes = false;
            LabEmpty.Text = "服务端还没有可用的中继节点";
            LabEmpty.Visibility = Visibility.Visible;
            UpdateButtons();
            return;
        }

        _hasNodes = true;
        ComboNodes.ItemsSource = nodes;
        ComboNodes.SelectedItem = nodes.FirstOrDefault(n => n.Id == selected) ?? nodes[0];
        if (ListPorts.ItemsSource is null) LabEmpty.Text = "还没有分配的端口";
        UpdateButtons();
    }

    private void ApplyQuota(RelayQuota quota)
    {
        LabTraffic.Text = $"{FormatSize(quota.UsedTrafficBytes)} / {FormatSize(quota.MonthlyTrafficBytes)}";
        LabRate.Text = quota.RateMbps > 0 ? $"{quota.RateMbps} Mbps" : "不限速";
        LabPorts.Text = $"{quota.PortUsed} / {quota.PortQuota}";
        BarTraffic.Value = quota.UsageRatio;
    }

    private void ApplyAllocations(IReadOnlyList<RelayAllocation> list)
    {
        var items = list.Select(a => new PortItem(a)).ToList();
        ListPorts.ItemsSource = items;
        LabEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 端口

    private async void BtnAllocate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_versionTooLow)
        {
            SetStatus("版本过低，需要更新");
            return;
        }
        if (!_session.IsValid)
        {
            SetStatus("请先登录");
            return;
        }

        if (_banned)
        {
            SetStatus("账号已被封禁，无法分配中继端口");
            return;
        }

        if (ComboNodes.SelectedItem is not RelayNode node || string.IsNullOrEmpty(node.Id))
        {
            SetStatus("请选择节点");
            return;
        }

        if (!int.TryParse(TextLocalPort.Text.Trim(), out var localPort) || localPort is < 1 or > 65535)
        {
            SetStatus("本地端口不合法");
            return;
        }

        if (_runner is not null)
        {
            SetStatus("已有隧道在运行，请先停止");
            return;
        }

        try
        {
            SetBusy(true, "正在分配端口…");
            var result = await _api.AllocateAsync(node.Id, "PCL 联机").ConfigureAwait(true);
            _runningAllocationId = result.Allocation.Id;

            var runner = new TunnelClient(result.Tunnel, localPort, "pcl-" + result.Allocation.Id);
            var error = await runner.StartAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            if (error is not null)
            {
                runner.Dispose();
                await _api.ReleaseAsync(result.Allocation.Id).ConfigureAwait(true);
                _runningAllocationId = null;
                SetStatus(error);
                return;
            }

            _runner = runner;
            BtnStop.Visibility = Visibility.Visible;
            SetStatus($"隧道已建立：{result.Allocation.GameAddress}（本地 {localPort}）");
            ApplyQuota(result.Quota);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnRelease_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PortItem item }) return;

        try
        {
            SetBusy(true, "正在释放…");
            if (_runningAllocationId == item.Allocation.Id) StopTunnel();
            await _api.ReleaseAsync(item.Allocation.Id).ConfigureAwait(true);
            SetStatus("已释放 " + item.Allocation.GameAddress);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnCopy_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PortItem item }) return;
        try
        {
            Clipboard.SetText(item.Allocation.GameAddress);
            SetStatus("已复制：" + item.Allocation.GameAddress);
        }
        catch (Exception)
        {
            SetStatus("复制失败，请手动选中地址");
        }
    }

    private async void BtnStop_Click(object sender, MouseButtonEventArgs e) => await StopAndReleaseAsync().ConfigureAwait(true);

    /// <summary>
    ///     停止按钮 = 停隧道 + 自动释放端口（不留存量分配，下次分配随机给新端口）。
    ///     释放失败只报状态栏，不回滚「已停止」这个事实。
    /// </summary>
    private async Task StopAndReleaseAsync()
    {
        var id = _runningAllocationId;
        StopTunnel();
        if (id is null)
        {
            SetStatus("隧道已停止");
            return;
        }

        try
        {
            SetBusy(true, "正在停止并释放端口…");
            await _api.ReleaseAsync(id).ConfigureAwait(true);
            SetStatus("隧道已停止，端口已自动释放");
        }
        catch (Exception ex)
        {
            SetStatus("隧道已停止，但端口释放失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>退出也视为停止：尽力释放占用的端口，失败留给服务端 TTL 兜底，不拦退出。</summary>
    private void OnAppExit()
    {
        var id = _runningAllocationId;
        _runner?.Dispose();
        _runner = null;
        if (id is not null && _session.IsValid)
        {
            try
            {
                _api.ReleaseAsync(id).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // 进程正在退出，释放失败由服务端分配 TTL 收口
            }
        }
    }

    private void StopTunnel()
    {
        _runner?.Dispose();
        _runner = null;
        _runningAllocationId = null;
        BtnStop.Visibility = Visibility.Collapsed;
        SetStatus("隧道已停止");
    }

    // ---------------------------------------------------------------- 界面状态

    /// <summary>账号卡上的登录状态，决定状态点颜色与文案。</summary>
    private enum AuthState
    {
        LoggedOut,
        Expired,
        LoggedIn,
        Banned
    }

    // 主题切换时颜色不变，这里直接给定值：灰 / 绿 / 橙 / 红
    private static readonly System.Windows.Media.Brush AuthDotIdle =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly System.Windows.Media.Brush AuthDotOk =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly System.Windows.Media.Brush AuthDotWarn =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly System.Windows.Media.Brush AuthDotBad =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));

    /// <summary>
    ///     把当前登录状态刷到账号卡上：状态点 + 状态词 + 一行细节。
    ///     细节里的邮箱、用户组来自服务端返回的 <see cref="RelayMe" />，会话有效期来自本地令牌。
    /// </summary>
    private void SetAuthState(AuthState state, RelayMe? me = null)
    {
        var email = me?.User.Email ?? _session.Email;

        switch (state)
        {
            case AuthState.LoggedOut:
                DotAuth.Fill = AuthDotIdle;
                LabAccount.Text = "未登录";
                LabAccountSub.Text = "登录后即可分配中继端口";
                break;

            case AuthState.Expired:
                DotAuth.Fill = AuthDotWarn;
                LabAccount.Text = "登录已过期";
                LabAccountSub.Text = "会话令牌已失效，请重新登录后再分配端口";
                break;

            case AuthState.Banned:
                DotAuth.Fill = AuthDotBad;
                LabAccount.Text = "账号已封禁";
                LabAccountSub.Text = string.IsNullOrWhiteSpace(email)
                    ? "无法分配中继端口，请联系管理员"
                    : $"{email} · 无法分配中继端口，请联系管理员";
                break;

            default:
                DotAuth.Fill = AuthDotOk;
                LabAccount.Text = "已登录";

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(email)) parts.Add(email);
                if (!string.IsNullOrWhiteSpace(me?.Quota.GroupName)) parts.Add("用户组 " + me!.Quota.GroupName);
                if (me?.Quota.CustomQuota == true) parts.Add("单独配额");
                if (_session.ExpiresAt != default)
                    parts.Add("会话有效期至 " + _session.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
                LabAccountSub.Text = parts.Count == 0 ? "可以分配中继端口" : string.Join(" · ", parts);
                break;
        }
    }

    private void ShowLogin(bool expired = false)
    {
        PanLogin.Visibility = Visibility.Visible;
        PanQuota.Visibility = Visibility.Collapsed;
        BtnLogout.Visibility = Visibility.Collapsed;
        SetAuthState(expired ? AuthState.Expired : AuthState.LoggedOut);
        BtnStop.Visibility = Visibility.Collapsed;
        _banned = false;
        UpdateButtons();
    }

    private void ShowLoggedIn()
    {
        PanLogin.Visibility = Visibility.Collapsed;
        PanQuota.Visibility = Visibility.Visible;
        BtnLogout.Visibility = Visibility.Visible;
        SetAuthState(AuthState.LoggedIn);
        UpdateButtons();
    }

    /// <summary>封禁、没有节点、版本过低、正在忙碌时，分配按钮都不能点。</summary>
    private void UpdateButtons()
    {
        BtnAllocate.IsEnabled = !_busy && !_banned && _hasNodes && !_versionTooLow;
        ComboNodes.IsEnabled = !_banned && _hasNodes;
        BtnLogout.IsEnabled = !_busy;
        BtnRefreshCaptcha.IsEnabled = !_busy;
        BtnSendCode.IsEnabled = !_busy;
        BtnLogin.IsEnabled = !_busy;
    }

    private void SetStatus(string text) => LabStatus.Text = text;

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        UpdateButtons();
        if (status is not null) LabStatus.Text = status;
    }

    /// <summary>按 1024 进制展示，统一保留一位小数，例如「15.0 MB / 1.0 GB」。</summary>
    private static string FormatSize(long bytes)
    {
        const long k = 1024L;
        if (bytes >= k * k * k) return $"{bytes / (double)(k * k * k):0.0} GB";
        if (bytes >= k * k) return $"{bytes / (double)(k * k):0.0} MB";
        if (bytes >= k) return $"{bytes / (double)k:0.0} KB";
        return bytes + " B";
    }

    /// <summary>列表项包装：直接绑 RelayAllocation 的话 UsageText 没地方放。</summary>
    public sealed class PortItem
    {
        public PortItem(RelayAllocation allocation)
        {
            Allocation = allocation;
        }

        public RelayAllocation Allocation { get; }
        public string GameAddress => Allocation.GameAddress;
        public string NodeName => Allocation.NodeName;
        public string Label => Allocation.Label;
        public string UsageText => FormatSize(Allocation.BytesIn + Allocation.BytesOut);
    }
}
