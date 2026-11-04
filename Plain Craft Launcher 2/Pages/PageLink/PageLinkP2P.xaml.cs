using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Link;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Lobby;

namespace PCL;

/// <summary>
///     P2P 直连页：房主与玩家各填一组相同的网络名称与密钥，由 EasyTier 建立点对点网络。
///     这条链路不经过任何启动器服务端，因此不需要登录。
/// </summary>
/// <remarks>
///     房主点「创建房间」会生成一段房间码（网络名称 + 密钥 + 端口），发给朋友后由对方粘贴即可加入，
///     不再需要双方对着屏幕抄三行参数。协议栈（IPv4 / IPv6）在本页单独选择，只影响本机。
/// </remarks>
public partial class PageLinkP2P : IPageView
{
    /// <summary>房主固定的虚拟 IP，EasyTier 启动参数里写死。</summary>
    private const string HostVirtualIp = "10.114.51.41";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _busy;
    private int _gamePort = 25565;
    private bool _isHost = true;
    private string _name = LinkCode.NewName();
    private string _secret = LinkCode.NewSecret();

    /// <summary>由代码改写控件状态时置位，避免回写配置或重复解析。</summary>
    private bool _suppressEvents;

    public PageLinkP2P()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshRuntimeAsync();
        Loaded += (_, _) => RefreshAll();
    }

    public void OnNavigatedTo(object? parameter)
    {
        RefreshAll();
    }

    public void OnNavigatedFrom()
    {
    }

    // ---------------------------------------------------------------- 初始化

    private void RefreshAll()
    {
        _suppressEvents = true;
        try
        {
            switch (Config.Link.IpMode)
            {
                case LinkIpMode.Ipv4Only:
                    RadioIpV4.Checked = true;
                    break;
                case LinkIpMode.Ipv6Only:
                    RadioIpV6.Checked = true;
                    break;
                default:
                    RadioIpAuto.Checked = true;
                    break;
            }

            TextPeer.Text = Config.Link.ManualPeer;
            TextListenPort.Text = Config.Link.ManualPort > 0
                ? Config.Link.ManualPort.ToString()
                : PeerAddress.DefaultPort.ToString();
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateIpHint();
        UpdateModeUi();
    }

    /// <summary>按当前是房主还是玩家，切换可见区域与文案。</summary>
    private void UpdateModeUi()
    {
        _isHost = RadioHost.Checked;

        PanHostCode.Visibility = _isHost ? Visibility.Visible : Visibility.Collapsed;
        PanJoinCode.Visibility = _isHost ? Visibility.Collapsed : Visibility.Visible;
        PanPort.Visibility = _isHost ? Visibility.Visible : Visibility.Collapsed;
        PanListenPort.Visibility = _isHost ? Visibility.Visible : Visibility.Collapsed;

        BtnStart.Text = _isHost ? "创建房间" : "加入房间";
        LabModeHint.Text = _isHost
            ? "游戏开在你的这台电脑上。创建房间后把房间码发给朋友，他们就能连进来。"
            : "游戏开在朋友那台电脑上。把房主给你的房间码粘贴到下面，再点「加入房间」。";

        if (!_isHost && string.IsNullOrWhiteSpace(TextJoinCode.Text))
            LabParseHint.Text = "还没有粘贴房间码。";

        UpdateCode();
        UpdatePeerHint();
    }

    /// <summary>对端地址的填写要求，以及纯直连的硬性限制。</summary>
    private void UpdatePeerHint()
    {
        var peerFilled = !string.IsNullOrWhiteSpace(TextPeer.Text);
        var peerValid = PeerAddress.TryParse(TextPeer.Text, out _, out _);

        var head = _isHost
            ? peerFilled
                ? peerValid
                    ? "已填对端地址，创建后会主动连过去。"
                    : "对端地址格式不对，改好之前先按「等对方连入」处理。"
                : "对端地址可以留空：把「你的 IP:监听端口」告诉朋友，让他来连你，记得在防火墙放行该端口。"
            : peerFilled
                ? peerValid
                    ? "地址看起来没问题，点「加入房间」即可。"
                    : "地址格式不对，应为 IP:端口，例如 1.2.3.4:11010。"
                : "必须填房主的地址，否则不知道往哪里连。";

        LabPeerHint.Text = head +
                           "\n纯直连不经过任何中继服务器；如果双方都在 NAT 后面、又没有公网 IPv4/IPv6，就连不上，那种情况请改用「中继联机」。";
    }

    /// <summary>重算房间码并刷新显示。玩家的码由粘贴决定，这里只展示解析结果。</summary>
    private void UpdateCode()
    {
        LabCode.Text = LinkCode.Build(_name, _secret, _gamePort);
    }

    private void UpdateIpHint()
    {
        LabIpHint.Text = Config.Link.IpMode switch
        {
            LinkIpMode.Ipv4Only =>
                "只走 IPv4，关闭 IPv6 监听与打洞。IPv6 线路不稳定、或者打洞反复失败时选这个。",
            LinkIpMode.Ipv6Only =>
                "只监听 IPv6，不监听 IPv4。需要你和对方都有可路由的 IPv6 地址，任何一方没有公网 IPv6 都连不上。",
            _ =>
                "同时尝试 IPv4 与 IPv6，由 EasyTier 自动挑选能打通的路径。没有特殊理由就保持自动。"
        };
        LabIpHint.Text += "\n协议只影响本机，对方用自己的设置；改动在下一次连接时生效。";
    }

    // ---------------------------------------------------------------- 交互

    private void RadioMode_Check(object sender, Common.RouteEventArgs e)
    {
        if (_suppressEvents || _busy) return;
        UpdateModeUi();
    }

    private void RadioIp_Check(object sender, Common.RouteEventArgs e)
    {
        if (_suppressEvents) return;

        Config.Link.IpMode = RadioIpV4.Checked ? LinkIpMode.Ipv4Only
            : RadioIpV6.Checked ? LinkIpMode.Ipv6Only
            : LinkIpMode.Auto;

        UpdateIpHint();
    }

    private void BtnRegen_Click(object sender, MouseButtonEventArgs e)
    {
        _name = LinkCode.NewName();
        _secret = LinkCode.NewSecret();
        UpdateCode();
        LabStatus.Text = "已生成新的房间码，之前发出去的码作废";
    }

    private void BtnCopyCode_Click(object sender, MouseButtonEventArgs e) =>
        CopyToClipboard(LabCode.Text, "房间码已复制");

    private void BtnCopyAddress_Click(object sender, MouseButtonEventArgs e) =>
        CopyToClipboard(LabAddress.Text, "地址已复制");

    private void CopyToClipboard(string text, string okMessage)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            LabStatus.Text = okMessage;
        }
        catch (Exception)
        {
            LabStatus.Text = "复制失败，请手动选中后按 Ctrl+C";
        }
    }

    private void TextGamePort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!int.TryParse(TextGamePort.Text.Trim(), out var port) || port is < 1 or > 65535)
            return;
        _gamePort = port;
        UpdateCode();
    }

    private void TextPeer_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        UpdatePeerHint();
    }

    private void TextJoinCode_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _busy) return;

        var raw = TextJoinCode.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            LabParseHint.Text = "还没有粘贴房间码。";
            return;
        }

        if (LinkCode.TryParse(raw, out var name, out var secret, out var port))
        {
            _name = name;
            _secret = secret;
            _gamePort = port;
            LabParseHint.Text = $"已识别：端口 {port}";
        }
        else
        {
            LabParseHint.Text = "房间码不完整或格式不对，应当有 18 位字符、中间用连字符分成 4 段。";
        }
    }

    // ---------------------------------------------------------------- 连接

    private void BtnStart_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;

        if (_isHost)
        {
            if (!int.TryParse(TextGamePort.Text.Trim(), out var port) || port is < 1 or > 65535)
            {
                LabStatus.Text = "游戏端口需要填 1-65535 之间的数字";
                return;
            }

            _gamePort = port;
        }
        else if (!LinkCode.TryParse(TextJoinCode.Text, out var name, out var secret, out var port2))
        {
            LabStatus.Text = "请先粘贴有效的房间码";
            return;
        }
        else
        {
            _name = name;
            _secret = secret;
            _gamePort = port2;
        }

        // 对端地址：玩家必填，房主留空表示只等对方连入
        var peers = Array.Empty<string>();
        var peerText = TextPeer.Text.Trim();
        if (peerText.Length > 0)
        {
            if (!PeerAddress.TryParse(peerText, out peers, out var peerError))
            {
                LabStatus.Text = peerError;
                UpdatePeerHint();
                return;
            }
        }
        else if (!_isHost)
        {
            LabStatus.Text = "请先填房主的地址，例如 1.2.3.4:11010";
            return;
        }

        if (!int.TryParse(TextListenPort.Text.Trim(), out var listenPort) || listenPort is < 1 or > 65535)
        {
            LabStatus.Text = "监听端口需要填 1-65535 之间的数字";
            return;
        }

        if (ETController.Precheck() != 0)
        {
            LabStatus.Text = "正在下载 EasyTier，下载完成后请再点一次";
            GameLink.DownloadEasyTier();
            return;
        }

        if (ETController.Status != ETState.Stopped) ETController.Exit();

        // 记住这次的填法，下次打开页面直接带出来
        Config.Link.ManualPeer = peerText;
        Config.Link.ManualPort = listenPort;

        // 这里不经过大厅服务端，直接构造一份等价的大厅信息交给 EasyTier。
        LobbyInfoProvider.TargetLobby = new LobbyInfoProvider.LobbyInfo
        {
            OriginalCode = $"{_name}-{_secret}",
            Type = LobbyInfoProvider.LobbyType.PCLCE,
            NetworkName = _name,
            NetworkSecret = _secret,
            Port = _gamePort,
            Ip = HostVirtualIp
        };

        if (ETController.Launch(_isHost, null, peers, listenPort) != 0)
        {
            LabStatus.Text = "启动 EasyTier 失败，请查看日志";
            return;
        }

        SetBusy(true);
        PanAddress.Visibility = Visibility.Visible;
        PanRuntime.Visibility = Visibility.Visible;
        LabAddress.Text = _isHost ? $"{HostVirtualIp}:{_gamePort}" : $"127.0.0.1:{LobbyInfoProvider.JoinerLocalPort}";
        LabAddressHint.Text = _isHost
            ? "在游戏里用「直接连接」填这个地址；另外把你的公网 IP 与监听端口告诉朋友"
            : "进游戏用「直接连接」填这个地址";
        LabStatus.Text = _isHost ? "正在创建房间…" : "正在加入房间…";
        LabPeerCount.Text = "—";
        LabPeerRoute.Text = "—";
        LabPeerPing.Text = "—";
        _timer.Start();
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e) => ResetToIdle("已断开");

    private void ResetToIdle(string message)
    {
        _timer.Stop();
        ETController.Exit();
        SetBusy(false);
        PanAddress.Visibility = Visibility.Collapsed;
        PanRuntime.Visibility = Visibility.Collapsed;
        LabStatus.Text = message;
    }

    private void SetBusy(bool connected)
    {
        _busy = connected;
        BtnStart.IsEnabled = !connected;
        BtnStop.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        RadioHost.IsEnabled = !connected;
        RadioJoin.IsEnabled = !connected;
        TextGamePort.IsEnabled = !connected;
        TextJoinCode.IsEnabled = !connected;
        TextPeer.IsEnabled = !connected;
        TextListenPort.IsEnabled = !connected;
        BtnCopyCode.IsEnabled = !connected;
    }

    // ---------------------------------------------------------------- 运行信息

    private async Task RefreshRuntimeAsync()
    {
        if (!_busy) return;

        // 进程被关掉或自己退出，界面不能一直停在「正在创建」
        var process = ETController.ETProcess;
        if (process is null || process.HasExited)
        {
            ResetToIdle("连接已结束，可以重新创建房间");
            return;
        }

        var result = await Task.Run(ETInfoProvider.GetPlayerList);
        var peers = result.Item1;
        if (peers is null) return; // 刚启动时 RPC 还没就绪，下一轮再试

        // Cost 为 Local 的那条是自己；能读到自己说明 RPC 已通，网络起来了
        var selfReady = peers.Any(p => p.Cost == ETConnectionType.Local);
        var others = peers.Where(p => p.Cost != ETConnectionType.Local).ToList();

        if (others.Count == 0)
        {
            LabPeerCount.Text = _isHost ? "还没有人加入" : "正在寻找房主";
            LabPeerRoute.Text = "—";
            LabPeerPing.Text = "—";
            if (selfReady)
                LabStatus.Text = _isHost ? "房间已创建，等待玩家加入" : "已进入网络，等待房主响应";
            return;
        }

        LabPeerCount.Text = _isHost ? $"{others.Count} 位玩家" : "已连上房主";

        // 玩家视角只关心与房主之间这一段；房主视角按所有玩家的最优路径汇总
        var sample = _isHost ? others : others.Where(p => p.IsHost).ToList();
        if (sample.Count == 0) sample = others;

        var p2pCount = sample.Count(p => p.Cost == ETConnectionType.P2P);
        var relayCount = sample.Count(p => p.Cost == ETConnectionType.Relay);
        LabPeerRoute.Text = p2pCount > 0 && relayCount > 0 ? "P2P + 中继"
            : p2pCount > 0 ? "P2P 直连"
            : relayCount > 0 ? "中继转发"
            : "协商中";

        var pings = sample.Where(p => p.Ping > 0).Select(p => p.Ping).ToList();
        LabPeerPing.Text = pings.Count == 0 ? "—" : $"{pings.Min():0} ms";

        LabStatus.Text = _isHost
            ? $"房间已建立，{others.Count} 位玩家在房间里"
            : "已连上房主，可以直接进游戏";
    }
}
