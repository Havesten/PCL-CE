using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Link;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Lobby;
using PCL.Core.Link.Scaffolding.EasyTier;

namespace PCL;

/// <summary>
///     房间码匹配网络身份，通过共享节点会合后由 EasyTier 尝试 P2P 打洞。
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
    private bool _starting;
    private bool _refreshing;
    private CancellationTokenSource? _natDetection;
    private CliNetTest.NetStatus? _natStatus;
    private DateTime _natCheckedAt;
    private DateTime _connectedAt;
    private DateTime? _peerSeenAt;
    private int _gamePort = 25565;
    private bool _isHost = true;
    private string _name = LinkCode.NewName();
    private string _secret = LinkCode.NewSecret();

    /// <summary>由代码改写控件状态时置位，避免回写配置或重复解析。</summary>
    private bool _suppressEvents;

    public PageLinkP2P()
    {
        _suppressEvents = true;
        InitializeComponent();
        _suppressEvents = false;
        _timer.Tick += async (_, _) => await RefreshRuntimeAsync();
        Loaded += (_, _) => { RefreshAll(); if (_busy) _timer.Start(); };
        Unloaded += (_, _) => { _timer.Stop(); _natDetection?.Cancel(); };
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

            if (!_busy)
            {
                if (Config.Link.RendezvousPeer == "tcp://public.easytier.top:11010")
                    Config.Link.RendezvousPeer = "tcp://et.starvm.top:11010";
                TextPeer.Text = RadioPunch.Checked ? Config.Link.RendezvousPeer : Config.Link.ManualPeer;
            }
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

    /// <summary>会合节点帮助双方交换地址，不要求双方持有独立公网 IP。</summary>
    private void UpdatePeerHint()
    {
        var peerFilled = !string.IsNullOrWhiteSpace(TextPeer.Text);
        var peerValid = PeerAddress.TryParse(TextPeer.Text, out _, out _);
        var punching = RadioPunch.Checked;

        LabPeerLabel.Text = punching ? "会合节点" : (_isHost ? "玩家地址" : "房主地址");
        TextPeer.HintText = punching
            ? "双方使用同一会合节点，例如 tcp://et.starvm.top:11010"
            : "输入对方可访问的公网 IP:端口或域名:端口";
        LabPeerHint.Text = peerFilled && peerValid
            ? punching
                ? "双方填写相同的会合节点并使用相同的房间码。会合节点只交换连接信息；游戏数据必须 P2P 直传，不会中继。"
                : "此地址只用于直接连接对方，不经过会合节点；需确保端口可从互联网访问。"
            : punching
                ? "请填写有效的 EasyTier 会合节点地址；双方必须能访问同一个节点。"
                : "请填写对方可访问的 IP 或域名及监听端口。";
        LabRendezvousHint.Text = punching
            ? "会合节点仅帮助双方发现和协商连接，不转发游戏数据。此模式强制 P2P 直连；打洞失败时不会回退中继，请改用左侧「中继联机」。"
            : "手动直连不使用会合节点或中继；双方必须预先知道对方地址并允许 EasyTier 监听端口通过防火墙。";
    }

    private void RadioConnection_Check(object sender, Common.RouteEventArgs e)
    {
        if (_suppressEvents || _busy) return;
        TextPeer.Text = RadioPunch.Checked ? Config.Link.RendezvousPeer : Config.Link.ManualPeer;
        UpdatePeerHint();
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

    private async void BtnStart_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _starting || _natDetection is not null) return;

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

        var punching = RadioPunch.Checked;
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
        else
        {
            LabStatus.Text = punching ? "请先填写双方共用的 EasyTier 会合节点" : "请先填写对方可访问的地址";
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

        _starting = true;
        SetBusy(false);
        try
        {
            if (punching && (_natStatus is null || DateTime.UtcNow - _natCheckedAt > TimeSpan.FromMinutes(2)))
                await DetectNatAsync();
            if (!IsLoaded) return;
            if (punching && _natStatus is null)
            {
                ShowConnectionError("NAT 检测失败，尚不能判断打洞条件。请重新检测或选择中继联机。");
                return;
            }
            if (punching && _natStatus!.UdpNatType == CliNetTest.NatType.Unknown &&
                _natStatus.TcpNatType == CliNetTest.NatType.Unknown &&
                (Config.Link.IpMode == LinkIpMode.Ipv4Only || !_natStatus.SupportIPv6))
            {
                ShowConnectionError("未能检测到可评估的 NAT 类型或公网 IPv6，暂不能确认打洞条件。请检查网络并重新检测，或使用中继联机。");
                return;
            }
            if (punching && Config.Link.IpMode == LinkIpMode.Ipv6Only && !_natStatus!.SupportIPv6)
            {
                ShowConnectionError("未检测到可用公网 IPv6，请改用自动或仅 IPv4 后重试。");
                return;
            }

        if (ETController.Status != ETState.Stopped) ETController.Exit();

        // 记住这次的填法，下次打开页面直接带出来
        if (punching) Config.Link.RendezvousPeer = peerText;
        else Config.Link.ManualPeer = peerText;
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

        if (ETController.Launch(_isHost, null, peers, listenPort, holePunching: punching) != 0)
        {
            LabStatus.Text = "启动 EasyTier 失败，请查看日志";
            return;
        }

        SetBusy(true);
        LoadConnection.Visibility = Visibility.Visible;
        LoadConnection.State.LoadingState = MyLoading.MyLoadingState.Run;
        PanAddress.Visibility = Visibility.Collapsed;
        PanRuntime.Visibility = Visibility.Visible;
        LabAddress.Text = _isHost ? $"{HostVirtualIp}:{_gamePort}" : $"127.0.0.1:{LobbyInfoProvider.JoinerLocalPort}";
        LabAddressHint.Text = _isHost
            ? "把房间码和会合节点发给朋友；朋友加入后使用自己页面显示的本地地址进游戏"
            : "进游戏用「直接连接」填这个地址";
        LabStatus.Text = _isHost ? "正在创建房间…" : "正在加入房间…";
        LabPeerCount.Text = "—";
        LabPeerRoute.Text = "—";
        LabPeerPing.Text = "—";
        LabRemoteNat.Text = "等待对方加入后读取其 NAT 类型。";
        _connectedAt = DateTime.UtcNow;
        _peerSeenAt = null;
        _timer.Start();
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Link] P2P 连接失败");
            ResetToIdle("启动连接失败，请查看日志或使用中继联机。");
        }
        finally
        {
            _starting = false;
            SetBusy(_busy);
        }
    }

    private async void BtnDetectNat_Click(object sender, MouseButtonEventArgs e)
    {
        if (_starting || _natDetection is not null) return;
        if (ETController.Precheck() != 0)
        {
            LabNatType.Text = "正在从官方发布页下载 EasyTier，完成后请再次检测。";
            GameLink.DownloadEasyTier();
            return;
        }
        await DetectNatAsync();
    }

    private async Task DetectNatAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _natDetection = cancellation;
        _natStatus = null;
        SetBusy(_busy);
        LabNatType.Text = "正在检测 UDP / TCP NAT 类型，最长约 30 秒…";
        LabNatAdvice.Text = "检测直接访问 STUN 服务，网络代理与实际游戏网络可能不同。";
        try
        {
            _natStatus = await CliNetTest.GetNetStatusAsync(cancellation.Token);
            _natCheckedAt = DateTime.UtcNow;
            LabNatType.Text = _natStatus is null ? "检测失败或超时：无法判断 NAT 类型。" :
                $"UDP：{CliNetTest.GetNatTypeString(_natStatus.UdpNatType)}　TCP：{CliNetTest.GetNatTypeString(_natStatus.TcpNatType)}\n公网 IPv6：{(_natStatus.SupportIPv6 ? "已检测到" : "未检测到")}";
            LabNatAdvice.Text = CliNetTest.GetPunchingAdvice(_natStatus?.UdpNatType ?? CliNetTest.NatType.Unknown)
                                + "\n请朋友也检测一次；NAT1 不是打洞的唯一可用类型，也不能保证任意两端一定成功。";
        }
        catch (OperationCanceledException)
        {
            _natStatus = null;
            LabNatType.Text = "检测已取消，可重新检测。";
        }
        finally
        {
            _natDetection = null;
            SetBusy(_busy);
        }
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e) => ResetToIdle("已断开", false);

    private void ShowConnectionError(string message)
    {
        LabStatus.Text = message;
        LoadConnection.TextError = message;
        LoadConnection.Visibility = Visibility.Visible;
        LoadConnection.State.LoadingState = MyLoading.MyLoadingState.Error;
    }

    private void ResetToIdle(string message, bool failed = true)
    {
        _timer.Stop();
        ETController.Exit();
        SetBusy(false);
        PanAddress.Visibility = Visibility.Collapsed;
        PanRuntime.Visibility = Visibility.Collapsed;
        LabStatus.Text = message;
        LoadConnection.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        LoadConnection.TextError = message;
        LoadConnection.State.LoadingState = failed ? MyLoading.MyLoadingState.Error : MyLoading.MyLoadingState.Stop;
        _peerSeenAt = null;
    }

    private void SetBusy(bool connected)
    {
        _busy = connected;
        var idle = !connected && !_starting && _natDetection is null;
        BtnStart.IsEnabled = idle;
        BtnDetectNat.IsEnabled = !_starting && _natDetection is null;
        BtnStop.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        RadioHost.IsEnabled = idle;
        RadioJoin.IsEnabled = idle;
        RadioIpAuto.IsEnabled = idle;
        RadioIpV4.IsEnabled = idle;
        RadioIpV6.IsEnabled = idle;
        TextGamePort.IsEnabled = idle;
        TextJoinCode.IsEnabled = idle;
        TextPeer.IsEnabled = idle;
        TextListenPort.IsEnabled = idle;
        BtnRegen.IsEnabled = idle;
        RadioPunch.IsEnabled = idle;
        RadioDirect.IsEnabled = idle;
    }

    // ---------------------------------------------------------------- 运行信息

    private async Task RefreshRuntimeAsync()
    {
        if (!_busy || _refreshing) return;
        _refreshing = true;
        try
        {

        // 进程被关掉或自己退出，界面不能一直停在「正在创建」
        var process = ETController.ETProcess;
        if (process is null || process.HasExited)
        {
            ResetToIdle("连接已结束，可以重新创建房间");
            return;
        }

        var result = await Task.Run(ETInfoProvider.GetPlayerList);
        if (!_busy || !ReferenceEquals(process, ETController.ETProcess)) return;
        var peers = result.Item1;
        if (peers is null) return; // 刚启动时 RPC 还没就绪，下一轮再试

        // Cost 为 Local 的那条是自己；能读到自己说明 RPC 已通，网络起来了
        var selfReady = peers.Any(p => p.Cost == ETConnectionType.Local);
        var others = P2pReadiness.RoomPeers(peers);

        if (others.Count == 0)
        {
            _peerSeenAt = null;
            PanAddress.Visibility = Visibility.Collapsed;
            LoadConnection.State.LoadingState = MyLoading.MyLoadingState.Run;
            LabPeerCount.Text = _isHost ? "还没有人加入" : "正在寻找房主";
            LabPeerRoute.Text = "—";
            LabPeerPing.Text = "—";
            if (selfReady)
                LabStatus.Text = _isHost ? "本机已启动，等待玩家加入后验证 P2P；尚未确认会合节点或对端连通。"
                    : "本机已启动，正在寻找房主；尚未确认会合节点或对端连通。";
            if (DateTime.UtcNow - _connectedAt > TimeSpan.FromSeconds(45))
                LabStatus.Text += " 请确认双方已启动、房间码一致，以及节点地址和端口可达；未发现对端不能据此判定 NAT 不支持。";
            return;
        }

        LabPeerCount.Text = _isHost ? $"发现 {others.Count} 位玩家" : "已发现房间节点";

        // 玩家视角只关心与房主之间这一段；房主视角按所有玩家的最优路径汇总
        var sample = _isHost ? others : others.Where(p => p.IsHost).ToList();
        if (sample.Count == 0)
        {
            _peerSeenAt = null;
            PanAddress.Visibility = Visibility.Collapsed;
            LoadConnection.State.LoadingState = MyLoading.MyLoadingState.Run;
            LabPeerRoute.Text = "等待房主";
            LabStatus.Text = "发现其他玩家，但尚未发现房主；请确认房主已启动且房间码一致。";
            return;
        }

        var p2pCount = sample.Count(p => p.Cost == ETConnectionType.P2P);
        var relayCount = sample.Count(p => p.Cost == ETConnectionType.Relay);
        var hostPathReady = _isHost ? p2pCount > 0 : P2pReadiness.CanJoinHost(peers);
        if (!hostPathReady)
        {
            PanAddress.Visibility = Visibility.Collapsed;
            LoadConnection.State.LoadingState = MyLoading.MyLoadingState.Run;
            _peerSeenAt ??= DateTime.UtcNow;
            LabPeerRoute.Text = "等待 P2P 直连";
            LabStatus.Text = "已发现对方，正在验证 P2P；游戏数据不会经节点中转。";
            if (DateTime.UtcNow - _peerSeenAt > TimeSpan.FromSeconds(45))
                ResetToIdle("当前网络环境未能建立 P2P 直连，无法完成本次组网。请检查双方防火墙或改用中继联机。");
            return;
        }
        _peerSeenAt = null;
        LabPeerRoute.Text = relayCount > 0 ? "P2P 直连（仍有节点等待直连，不使用中继）" : "P2P 直连";

        var pings = sample.Where(p => p.Cost == ETConnectionType.P2P && p.Ping > 0).Select(p => p.Ping).ToList();
        LabPeerPing.Text = pings.Count == 0 ? "—" : $"{pings.Min():0} ms";
        LabRemoteNat.Text = "对端 NAT：" + string.Join("、", sample.Select(p =>
            string.IsNullOrWhiteSpace(p.NatType) ? "未知" : LobbyTextHandler.GetNatTypeName(p.NatType)));

        PanAddress.Visibility = Visibility.Visible;
        LoadConnection.State.LoadingState = MyLoading.MyLoadingState.Stop;
        LabStatus.Text = _isHost
            ? $"已验证 {p2pCount} 位玩家 P2P 直连；{sample.Count - p2pCount} 位仍在等待直连，不会经节点中转。"
            : "已连上房主，可以直接进游戏";
        }
        finally { _refreshing = false; }
    }
}
