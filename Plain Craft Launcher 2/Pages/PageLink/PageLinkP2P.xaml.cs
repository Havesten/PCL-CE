using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.IoC;
using PCL.Core.Link;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Lobby;
using PCL.Core.Link.Relay;
using PCL.Core.Link.Scaffolding.EasyTier;
using PCL.Core.UI.Dialogs;
using PCL.Core.Logging;
using Microsoft.Win32;

namespace PCL;

public partial class PageLinkP2P : IPageView
{
    private readonly RelayApi _api = new();
    private RelaySession _session = RelaySession.Load();
    private RelayStatus? _policy;
    private RelayCaptcha? _captcha;
    private CliNetTest.NetStatus? _natStatus;
    private DateTime _natCheckedAt;
    private IReadOnlyList<RelayGameServer> _games = [];
    private P2pCloudRoom? _room;
    private P2pRoomLease? _lease;
    private P2pGameProxy? _hostBridge;
    private P2pGameProxy? _guestEntrance;
    private int _bridgePort;
    private P2pLanPublisher? _lan;
    private Process? _process;
    private CancellationTokenSource? _connection;
    private Task? _monitor;
    private Task? _startup;
    private Task? _shutdown;
    private CancellationTokenSource? _attempt;
    private Action<string>? _leaseClosed;
    private Action<string?>? _leaseChanged;
    private int _networkChanged;
    private int _transportReady;
    private string _networkSignature = "";
    private readonly object _launchState = new();
    private long _generation;
    private bool _starting;
    private bool _working;
    private bool _detecting;
    private bool _connected;
    private bool _stopping;
    private volatile bool _exiting;
    private bool _suppress;
    private bool _pathFailed;
    private bool _cancelledByUser;
    private readonly CancellationTokenSource _operations = new();
    private bool IsHost => RadioHost.Checked;

    public PageLinkP2P()
    {
        _suppress = true;
        InitializeComponent();
        if (Config.Link.IpMode == LinkIpMode.Ipv4Only) RadioIpV4.Checked = true;
        else if (Config.Link.IpMode == LinkIpMode.Ipv6Only) RadioIpV6.Checked = true;
        _suppress = false;
        Loaded += async (_, _) => { UpdateMode(); await RefreshConfigAsync(); };
        Application.Current.Exit += (_, _) => OnExit();
        Lifecycle.When(LifecycleState.Exiting, OnExit);
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }
    public async void OnNavigatedTo(object? parameter) { await RefreshConfigAsync(); }
    public void OnNavigatedFrom() { }

    private async Task RefreshConfigAsync()
    {
        if (_working || _exiting) return;
        _working = true;
        try
        {
            _session = RelaySession.Load();
            _api.Token = _session.IsValid ? _session.Token : null;
            _policy = await _api.GetStatusAsync(_operations.Token);
            LabAccount.Text = _session.IsValid ? _session.Email : "登录后可创建或加入房间";
            BtnAccount.Text = _session.IsValid ? "退出登录" : "登录账号";
            if (!_policy.P2pEnabled) LabStatus.Text = "P2P 联机已由管理员关闭";
            else if (!_policy.P2pRoomCodes) LabStatus.Text = "请先升级云端以启用 P2P 房间码";
            else if (_policy.RequiredVersion is { Length: > 0 } version && version != Basics.VersionName)
                LabStatus.Text = "版本过低，需要更新";
            else if (_policy.RegionCheck && !_policy.RegionAllowed)
                LabStatus.Text = "当前地区无法使用联机功能";
            else if (_session.IsValid && !_connected)
            {
                var selected = (ComboNodes.SelectedItem as P2pRendezvousNode)?.Id;
                var nodes = await _api.GetP2pNodesAsync(_operations.Token);
                ComboNodes.ItemsSource = nodes;
                ComboNodes.SelectedItem = nodes.FirstOrDefault(n => n.Id == selected) ?? nodes.FirstOrDefault();
                if (nodes.Count == 0) LabStatus.Text = "暂无可用会合节点，请联系管理员";
            }
        }
        catch (OperationCanceledException) when (_operations.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _working = false; UpdateButtons(); }
    }

    private void RadioMode_Check(object sender, Common.RouteEventArgs e) { if (!_suppress) UpdateMode(); }
    private void UpdateMode()
    {
        PanHost.Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
        TextJoinCode.Visibility = IsHost ? Visibility.Collapsed : Visibility.Visible;
        LabModeHint.Text = IsHost ? "请在游戏内开启局域网联机。" : "输入房间码，连接后在游戏的多人游戏列表进入。";
        BtnStart.Text = IsHost ? "创建房间" : "加入房间";
        BtnStop.Text = IsHost ? "关闭房间" : "退出房间";
        UpdateButtons();
    }
    private void RadioIp_Check(object sender, Common.RouteEventArgs e)
    {
        if (_suppress) return;
        Config.Link.IpMode = RadioIpV4.Checked ? LinkIpMode.Ipv4Only : RadioIpV6.Checked ? LinkIpMode.Ipv6Only : LinkIpMode.Auto;
        _pathFailed = false;
        LabNatAdvice.Text = P2pReadiness.Assess(_natStatus, Config.Link.IpMode).Summary;
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var idle = !_working && !_detecting && !_connected && !_stopping && !_exiting;
        var permitted = _policy is { P2pEnabled: true, P2pRoomCodes: true } &&
            (!_policy.RegionCheck || _policy.RegionAllowed) &&
            (string.IsNullOrEmpty(_policy.RequiredVersion) || _policy.RequiredVersion == Basics.VersionName);
        BtnStart.IsEnabled = idle && permitted && _session.IsValid && !_pathFailed &&
            P2pReadiness.Assess(_natStatus, Config.Link.IpMode).CanAttempt;
        BtnDetectNat.IsEnabled = !_working && !_detecting && !_stopping && !_exiting;
        BtnStop.Visibility = _connected || _starting ? Visibility.Visible : Visibility.Collapsed;
        BtnStop.Text = _starting ? "取消连接" : IsHost ? "关闭房间" : "退出房间";
        BtnStop.IsEnabled = (_starting || !_working) && !_stopping && !_exiting;
        RadioHost.IsEnabled = idle;
        RadioJoin.IsEnabled = idle;
        RadioIpAuto.IsEnabled = idle;
        RadioIpV4.IsEnabled = idle;
        RadioIpV6.IsEnabled = idle;
        TextJoinCode.IsEnabled = idle;
        ComboNodes.IsEnabled = idle;
        ComboGames.IsEnabled = idle;
        BtnAccount.IsEnabled = idle;
        BtnLogin.IsEnabled = idle;
        BtnCaptcha.IsEnabled = idle;
        BtnSendCode.IsEnabled = idle;
    }

    private async void BtnAccount_Click(object sender, MouseButtonEventArgs e)
    {
        if (_working || _connected) return;
        if (_session.IsValid)
        {
            _session.Clear(); _api.Token = null;
            await RefreshConfigAsync();
        }
        else
        {
            PanLogin.Visibility = PanLogin.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (PanLogin.Visibility == Visibility.Visible) await LoadCaptchaAsync();
        }
    }
    private async Task LoadCaptchaAsync()
    {
        try { _captcha = await _api.GetCaptchaAsync(_operations.Token); LabCaptcha.Text = _captcha.Prompt; TextCaptcha.Text = ""; }
        catch (OperationCanceledException) when (_operations.IsCancellationRequested) { }
        catch (Exception ex) { if (!_exiting) LabCaptcha.Text = ex.Message; }
    }
    private async void BtnCaptcha_Click(object sender, MouseButtonEventArgs e) => await LoadCaptchaAsync();
    private async void BtnSendCode_Click(object sender, MouseButtonEventArgs e)
    {
        if (_working || _captcha is null) return;
        _working = true; UpdateButtons();
        try
        {
            await _api.SendCodeAsync(TextEmail.Text.Trim(), _captcha.Id, TextCaptcha.Text.Trim(), _operations.Token);
            LabStatus.Text = "验证码已发送，请查看邮箱";
            await LoadCaptchaAsync();
        }
        catch (OperationCanceledException) when (_operations.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); await LoadCaptchaAsync(); }
        finally { _working = false; UpdateButtons(); }
    }
    private async void BtnLogin_Click(object sender, MouseButtonEventArgs e)
    {
        if (_working) return;
        _working = true; UpdateButtons();
        try
        {
            var result = await _api.LoginAsync(TextEmail.Text.Trim(), TextEmailCode.Text.Trim(), _operations.Token);
            _session.Token = result.Token; _session.Email = TextEmail.Text.Trim(); _session.ExpiresAt = result.ExpiresAt; _session.Save();
            PanLogin.Visibility = Visibility.Collapsed;
            LabStatus.Text = "登录成功";
        }
        catch (OperationCanceledException) when (_operations.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _working = false; }
        await RefreshConfigAsync();
    }

    private void ShowError(Exception ex)
    {
        if (_exiting) return;
        if (ex is RelayApiException { Status: HttpStatusCode.Unauthorized })
        {
            _session.Clear(); _api.Token = null;
            LabAccount.Text = "登录已过期，请重新登录"; BtnAccount.Text = "登录账号";
            PanLogin.Visibility = Visibility.Visible;
        }
        LabStatus.Text = ex.Message;
    }

    private async Task<bool> CheckAccountAsync(CancellationToken ct)
    {
        _session = RelaySession.Load();
        _api.Token = _session.IsValid ? _session.Token : null;
        if (!_session.IsValid) throw new InvalidOperationException("请先登录账号");
        var me = await _api.GetMeAsync(ct);
        if (me.IsBanned) throw new InvalidOperationException("账号已被封禁");
        if (!me.PhoneRequired) return true;
        await DialogService.Instance.ShowAsync(new PhoneBindDialogRequest
        {
            Title = "绑定手机号", Content = "请绑定手机号后使用联机功能。", ForceWait = true,
            Buttons = DialogButtons.From(DialogButton.Create("绑定手机号"), DialogButton.Create("稍后再说")),
            GetCaptchaAsync = async () => { var cap = await _api.GetCaptchaAsync(ct); return (cap.Id, cap.Prompt); },
            SendSmsAsync = (phone, id, answer) => _api.SendSmsCodeAsync(phone, id, answer, ct),
            BindAsync = (phone, code) => _api.BindPhoneAsync(phone, code, ct)
        }).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return !(await _api.GetMeAsync(ct)).PhoneRequired;
    }

    private async void BtnDetectNat_Click(object sender, MouseButtonEventArgs e)
    {
        if (_working || _detecting) return;
        try { await DetectNatAsync(_operations.Token); }
        catch (OperationCanceledException) { }
    }
    private async Task DetectNatAsync(CancellationToken ct)
    {
        if (ETController.Precheck() != 0)
        {
            LabNatType.Text = "正在准备联机组件，请完成后重新检测。";
            GameLink.DownloadEasyTier(); return;
        }
        _detecting = true; UpdateButtons();
        LabNatType.Text = "正在检测…"; LabNatAdvice.Text = "";
        try
        {
            _natStatus = await CliNetTest.GetNetStatusAsync(ct);
            _natCheckedAt = DateTime.UtcNow;
            _pathFailed = false;
            LabNatType.Text = _natStatus is null ? "检测失败或超时" :
                $"UDP：{CliNetTest.GetNatTypeString(_natStatus.UdpNatType)}　TCP：{CliNetTest.GetNatTypeString(_natStatus.TcpNatType)}\nIPv6：{(_natStatus.SupportIPv6 ? "可用" : "未检测到")}";
            LabNatAdvice.Text = P2pReadiness.Assess(_natStatus, Config.Link.IpMode).Summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { if (!_exiting) LabNatType.Text = ex.Message; }
        finally { _detecting = false; UpdateButtons(); }
    }

    private async void BtnStart_Click(object sender, MouseButtonEventArgs e)
    {
        if (_working || _connected || _detecting || _pathFailed || _stopping || _exiting) return;
        _working = true; _starting = true;
        _cancelledByUser = false;
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(_operations.Token);
        _attempt.CancelAfter(TimeSpan.FromSeconds(120));
        var generation = Interlocked.Increment(ref _generation);
        UpdateButtons();
        _startup = StartCoreAsync(IsHost, generation, _attempt.Token);
        try { await _startup; }
        finally
        {
            _starting = false; _working = false; _startup = null;
            _attempt.Dispose(); _attempt = null;
            UpdateButtons();
        }
    }

    private async Task StartCoreAsync(bool hosting, long generation, CancellationToken ct)
    {
        P2pCloudRoom? allocated = null;
        var ownedByLease = false;
        try
        {
            _policy = await _api.GetStatusAsync(ct);
            if (!_policy.P2pEnabled || !_policy.P2pRoomCodes) throw new InvalidOperationException("P2P 联机暂不可用，请联系管理员");
            if (_policy.RegionCheck && !_policy.RegionAllowed) throw new InvalidOperationException("当前地区无法使用联机功能");
            if (!string.IsNullOrEmpty(_policy.RequiredVersion) && _policy.RequiredVersion != Basics.VersionName)
                throw new InvalidOperationException("版本过低，需要更新");
            if (!await CheckAccountAsync(ct)) { LabStatus.Text = "绑定手机号后可使用联机"; return; }
            ct.ThrowIfCancellationRequested();
            if (ETController.Precheck() != 0) { GameLink.DownloadEasyTier(); LabStatus.Text = "正在准备联机组件，请完成后重试"; return; }
            if (_natStatus is null || DateTime.UtcNow - _natCheckedAt > TimeSpan.FromMinutes(2)) await DetectNatAsync(ct);
            ct.ThrowIfCancellationRequested();
            var assessment = P2pReadiness.Assess(_natStatus, Config.Link.IpMode);
            if (!assessment.CanAttempt) throw new InvalidOperationException(assessment.Summary);
            if (ETController.Status != ETState.Stopped) throw new InvalidOperationException("已有 P2P 连接，请先断开");
            RelayGameServer? game = null;
            if (hosting)
            {
                var nodes = await _api.GetP2pNodesAsync(ct);
                var selected = ComboNodes.SelectedItem as P2pRendezvousNode;
                var node = nodes.FirstOrDefault(n => n.Id == selected?.Id) ?? nodes.FirstOrDefault();
                if (node is null) throw new InvalidOperationException("暂无可用会合节点，请联系管理员");
                var selectedGame = (ComboGames.SelectedItem as RelayGameServer)?.Endpoint;
                LabStatus.Text = "正在识别游戏…";
                _games = await RelayGameDiscovery.ScanAsync(ct);
                ComboGames.ItemsSource = _games;
                ComboGames.Visibility = _games.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                ComboGames.SelectedItem = _games.FirstOrDefault(g => g.Endpoint.Equals(selectedGame)) ?? (_games.Count == 1 ? _games[0] : null);
                game = ComboGames.SelectedItem as RelayGameServer ?? (_games.Count == 1 ? _games[0] : null);
                if (game is null) { LabStatus.Text = _games.Count == 0 ? "请在游戏内开启局域网联机" : "请选择要分享的游戏，再创建房间"; return; }
                var verified = await MinecraftServerProbe.ProbeAsync(game.Endpoint, TimeSpan.FromSeconds(3), ct);
                if (verified is null) { _games = []; throw new InvalidOperationException("游戏已关闭，请重新开启局域网联机"); }
                lock (_launchState)
                {
                    ct.ThrowIfCancellationRequested();
                    _hostBridge = new P2pGameProxy(game.Endpoint);
                    _hostBridge.Start();
                    _bridgePort = _hostBridge.LocalEndpoint.Port;
                }
                allocated = await _api.CreateP2pRoomAsync(node.Id, game.Name, game.Version, _bridgePort, ct);
                _room = allocated;
            }
            else
            {
                LabStatus.Text = "正在寻找房主…";
                _room = await _api.JoinP2pRoomAsync(TextJoinCode.Text, ct);
            }
            ValidateRoom(_room);
            ct.ThrowIfCancellationRequested();
            var peer = _room.RendezvousUri;
            peer = await ResolvePeerAsync(peer, Config.Link.IpMode, ct);
            lock (_launchState)
            {
                ct.ThrowIfCancellationRequested();
                LabStatus.Text = hosting ? "正在创建房间…" : "正在建立 P2P 直连…";
                ConfigureLobby(_room);
                if (ETController.Launch(hosting, peers: [peer], listenPort: 0, holePunching: true) != 0)
                    throw new InvalidOperationException("联机组件启动失败");
                _process = ETController.ETProcess ?? throw new InvalidOperationException("联机组件启动失败");
                _connection = CancellationTokenSource.CreateLinkedTokenSource(_operations.Token);
                _connected = true;
                Interlocked.Exchange(ref _transportReady, 1);
            }
            var process = _process;
            var hostGame = game;
            P2pRoomLease lease;
            lock (_launchState)
            {
                ct.ThrowIfCancellationRequested();
                lease = new P2pRoomLease(_api, _room, hosting, async token => hostGame is not null &&
                    await MinecraftServerProbe.ProbeAsync(hostGame.Endpoint, TimeSpan.FromSeconds(3), token) is not null,
                    () => _process is { } transport && IsProcessHealthy(transport) && Volatile.Read(ref _transportReady) != 0);
                _lease = lease; ownedByLease = true;
                _leaseClosed = message => Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (generation == Interlocked.Read(ref _generation) && ReferenceEquals(_lease, lease) && !_exiting)
                        await StopAsync(message);
                }));
                lease.Closed += _leaseClosed;
                _leaseChanged = message => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation == Interlocked.Read(ref _generation) && ReferenceEquals(_lease, lease) && !_exiting)
                    {
                        LabCloudStatus.Text = message ?? "";
                        LabCloudStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
                    }
                }));
                lease.Changed += _leaseChanged;
                lease.Report(hosting ? "hosting" : "waiting_host");
            }
            await lease.StartAsync(ct);
            lock (_launchState)
            {
                ct.ThrowIfCancellationRequested();
                if (!hosting)
                {
                    _guestEntrance = new P2pGameProxy(new IPEndPoint(IPAddress.Loopback, LobbyInfoProvider.JoinerLocalPort), acceptLocalInterfaces: true);
                    _guestEntrance.Start();
                    _lan = new P2pLanPublisher(_room.Name, _guestEntrance.LocalEndpoint.Port);
                }
            }
            LabCode.Text = _room.Code; PanCode.Visibility = Visibility.Visible;
            LabStatus.Text = hosting ? "房间已创建，请分享房间码" : "正在连接房主…";
            var monitorToken = _connection.Token;
            var lan = _lan;
            var entrance = _guestEntrance?.LocalEndpoint;
            var room = _room;
            var localForwardPort = LobbyInfoProvider.JoinerLocalPort;
            _networkSignature = ReadNetworkSignature();
            Interlocked.Exchange(ref _networkChanged, 0);
            _monitor = Task.Run(() => MonitorAsync(process, room, peer, localForwardPort, lease,
                hosting, lan, entrance, generation, monitorToken));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await StopAsync(_exiting || _attempt?.IsCancellationRequested == true && _cancelledByUser ? "已取消连接" : "连接准备超时，请检查网络后重试"); }
        catch (Exception ex)
        {
            await StopAsync(ex.Message);
            ShowError(ex);
        }
        finally
        {
            if (allocated is not null && !ownedByLease)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _api.ReleaseP2pRoomAsync(allocated.Id, budget.Token); } catch (Exception) { }
            }
        }
    }

    private static void ValidateRoom(P2pCloudRoom? room)
    {
        if (room is null || string.IsNullOrWhiteSpace(room.Id) ||
            !RelayRoomCode.TryNormalize(room.Code, out _) || room.GamePort is < 1 or > 65535 ||
            !Uri.TryCreate(room.RendezvousUri, UriKind.Absolute, out var uri) || uri.Scheme is not ("tcp" or "udp") ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.Length == 0 || uri.AbsolutePath is not ("" or "/") ||
            room.NetworkName is not { Length: 32 } || room.NetworkSecret is not { Length: 64 } ||
            !room.NetworkName.All(char.IsAsciiHexDigit) || !room.NetworkSecret.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("云端返回的房间信息无效");
    }

    private static bool IsProcessHealthy(Process process)
    {
        try { return ReferenceEquals(process, ETController.ETProcess) && !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }
    private static void ConfigureLobby(P2pCloudRoom room) => LobbyInfoProvider.TargetLobby = new LobbyInfoProvider.LobbyInfo
    {
        OriginalCode = room.Code, Type = LobbyInfoProvider.LobbyType.PCLCE,
        NetworkName = room.NetworkName, NetworkSecret = room.NetworkSecret, Port = room.GamePort, Ip = "10.114.51.41"
    };
    private static async Task<string> ResolvePeerAsync(string peer, LinkIpMode mode, CancellationToken ct)
    {
        if (mode != LinkIpMode.Ipv6Only) return peer;
        var uri = new Uri(peer);
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        if (address is null) throw new InvalidOperationException("会合节点未提供 IPv6，请改用自动");
        return $"{uri.Scheme}://[{address}]:{uri.Port}";
    }
    private static string ReadNetworkSignature()
    {
        try
        {
            return string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not
                    (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (n.Id, Properties: n.GetIPProperties()))
                .Where(n => n.Properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                .Select(n => n.Id + ":" + string.Join(",", n.Properties.UnicastAddresses
                    .Select(a => a.Address.ToString()).OrderBy(a => a)))
                .OrderBy(n => n));
        }
        catch (NetworkInformationException) { return ""; }
    }
    private void CheckNetworkChange()
    {
        var signature = ReadNetworkSignature();
        var previous = Interlocked.Exchange(ref _networkSignature, signature);
        if (previous != signature)
        { Interlocked.Exchange(ref _networkChanged, 1); Interlocked.Exchange(ref _transportReady, 0); }
    }
    private static string PhaseCode(P2pConnectionPhase phase) => phase switch
    {
        P2pConnectionPhase.WaitingHost => "waiting_host", P2pConnectionPhase.Connecting => "connecting",
        P2pConnectionPhase.Validating => "validating", P2pConnectionPhase.Connected => "connected",
        P2pConnectionPhase.Hosting => "hosting", P2pConnectionPhase.Reconnecting => "reconnecting", _ => "failed"
    };
    private void NetworkAddressChanged(object? sender, EventArgs e) => CheckNetworkChange();
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => CheckNetworkChange();
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        { Interlocked.Exchange(ref _networkChanged, 1); Interlocked.Exchange(ref _transportReady, 0); }
    }

    private async Task MonitorAsync(Process process, P2pCloudRoom room, string peer, int localForwardPort, P2pRoomLease lease,
        bool hosting, P2pLanPublisher? lan, IPEndPoint? entrance, long generation, CancellationToken ct)
    {
        string? ended = null;
        var pathFailed = false;
        var recovery = new P2pConnectionRecovery(hosting);
        var elapsed = Stopwatch.StartNew();
        P2pRecoveryDecision? previous = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var componentHealthy = IsProcessHealthy(process);
                List<ETPlayerInfo>? peers = null;
                if (componentHealthy)
                {
                    try { peers = (await Task.Run(ETInfoProvider.GetPlayerList, ct)).Item1; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { }
                }
                if (ct.IsCancellationRequested) break;
                var others = P2pReadiness.RoomPeers(peers ?? []);
                var direct = hosting ? others.Any(p => p.Cost == ETConnectionType.P2P) : P2pReadiness.CanJoinHost(peers ?? []);
                var gameReady = hosting || direct && entrance is not null &&
                    await MinecraftServerProbe.ProbeAsync(entrance, TimeSpan.FromSeconds(3), ct) is not null;
                var networkChanged = Interlocked.Exchange(ref _networkChanged, 0) != 0;
                var networkAvailable = NetworkInterface.GetIsNetworkAvailable();
                var decision = recovery.Observe(elapsed.Elapsed, componentHealthy && peers is not null && networkAvailable,
                    others.Count > 0, direct, gameReady, networkChanged, networkAvailable);
                var phase = PhaseCode(decision.Phase);
                Interlocked.Exchange(ref _transportReady, networkAvailable && decision.Phase is
                    (P2pConnectionPhase.Hosting or P2pConnectionPhase.Connected) ? 1 : 0);
                var failure = decision.FailureCode ?? (decision.Phase == P2pConnectionPhase.Reconnecting ?
                    componentHealthy ? "connection_lost" : "process_exited" : null);
                lease.Report(phase, failure, decision.RetryCount);
                if (previous?.Phase != decision.Phase || previous?.RetryCount != decision.RetryCount)
                {
                    LogWrapper.Info("Link", $"P2P 状态={phase}, 诊断={failure ?? "none"}, 重试={decision.RetryCount}");
                    previous = decision;
                }
                if (lan is not null) lan.Enabled = decision.Phase == P2pConnectionPhase.Connected;
                if (decision.Phase == P2pConnectionPhase.Failed)
                {
                    pathFailed = decision.FailureCode == "connect_timeout";
                    ended = decision.FailureCode == "game_unavailable" ? "游戏验证超时，请确认房主已开启局域网联机" :
                        "P2P 连接或恢复超时，请重新检测网络后重试";
                    using var reportBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reportBudget.CancelAfter(TimeSpan.FromSeconds(3));
                    try { await _api.GetP2pRoomStatusAsync(room.Id, phase, failure, decision.RetryCount, reportBudget.Token); } catch (Exception) { }
                    break;
                }
                if (decision.Restart)
                {
                    try { peer = await ResolvePeerAsync(room.RendezvousUri, Config.Link.IpMode, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { continue; }
                    lock (_launchState)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (generation != Interlocked.Read(ref _generation)) break;
                        if (ReferenceEquals(process, ETController.ETProcess)) ETController.Exit();
                        if (ETController.Status != ETState.Stopped) { ended = "其他联机连接正在运行，请先断开"; break; }
                        ConfigureLobby(room);
                        if (ETController.Launch(hosting, peers: [peer], listenPort: 0, holePunching: true, localForwardPort: localForwardPort) == 0)
                        {
                            process = ETController.ETProcess!;
                            _process = process;
                        }
                    }
                }
                var text = decision.Phase switch
                {
                    P2pConnectionPhase.Hosting => direct ? "玩家已连接" : "房间运行中，等待玩家加入",
                    P2pConnectionPhase.Connected => "已连接，请在游戏的多人游戏列表进入",
                    P2pConnectionPhase.WaitingHost => "正在寻找房主…",
                    P2pConnectionPhase.Connecting => "正在建立 P2P 直连…",
                    P2pConnectionPhase.Validating => "正在验证游戏…",
                    _ => "连接中断，正在自动恢复；恢复后请重新进入游戏。"
                };
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation == Interlocked.Read(ref _generation) && _connected && !_stopping && !_working && !_exiting)
                        LabStatus.Text = text;
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { ended = "连接状态读取失败，请重新连接"; }
        if (ended is not null && !_exiting) Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (generation != Interlocked.Read(ref _generation) || _exiting) return;
            _pathFailed = pathFailed;
            if (pathFailed) LabNatAdvice.Text = "本次连接不可用，请重新检测网络或更改协议后重试。";
            await StopAsync(ended);
        }));
    }

    private async void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (_starting)
        {
            _cancelledByUser = true; _attempt?.Cancel();
            BtnStop.IsEnabled = false; LabStatus.Text = "正在取消连接…";
            if (_startup is { } startup) await startup;
            return;
        }
        await StopAsync(IsHost ? "房间已关闭" : "已退出房间");
    }
    private Task StopAsync(string message)
    {
        if (_shutdown is { IsCompleted: false }) return _shutdown;
        _shutdown = StopCoreAsync(message);
        return _shutdown;
    }
    private async Task StopCoreAsync(string message)
    {
        _stopping = true; UpdateButtons();
        lock (_launchState)
        {
            Interlocked.Increment(ref _generation);
            _attempt?.Cancel();
            _connection?.Cancel();
            Interlocked.Exchange(ref _transportReady, 0);
            _lan?.Dispose(); _lan = null;
            if (_process is not null && ReferenceEquals(_process, ETController.ETProcess)) ETController.Exit();
            _process = null;
        }
        if (_monitor is not null) try { await _monitor; } catch (Exception) { }
        _monitor = null;
        if (_lease is not null)
        {
            if (_leaseClosed is not null) _lease.Closed -= _leaseClosed;
            if (_leaseChanged is not null) _lease.Changed -= _leaseChanged;
            await _lease.DisposeAsync();
        }
        _leaseClosed = null;
        _leaseChanged = null;
        _lease = null; _room = null;
        if (_hostBridge is not null) await _hostBridge.DisposeAsync(); _hostBridge = null;
        if (_guestEntrance is not null) await _guestEntrance.DisposeAsync(); _guestEntrance = null;
        _bridgePort = 0;
        _connection?.Dispose(); _connection = null;
        _connected = false;
        _games = []; ComboGames.ItemsSource = _games; ComboGames.Visibility = Visibility.Collapsed;
        PanCode.Visibility = Visibility.Collapsed;
        LabCloudStatus.Visibility = Visibility.Collapsed;
        LabStatus.Text = message;
        _stopping = false; UpdateButtons();
    }
    private void BtnCopyCode_Click(object sender, MouseButtonEventArgs e)
    {
        if (_room is null) return;
        try { Clipboard.SetText(_room.Code); LabStatus.Text = "房间码已复制"; }
        catch (Exception) { LabStatus.Text = "复制失败，请重试"; }
    }
    private void OnExit()
    {
        Task? leaseCleanup;
        lock (_launchState)
        {
            if (_exiting) return;
            _exiting = true; Interlocked.Increment(ref _generation);
            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            _operations.Cancel(); _connection?.Cancel(); _lan?.Dispose();
            if (_hostBridge is not null) _ = _hostBridge.DisposeAsync();
            if (_guestEntrance is not null) _ = _guestEntrance.DisposeAsync();
            if (_process is not null && ReferenceEquals(_process, ETController.ETProcess)) ETController.Exit();
            leaseCleanup = _lease?.DisposeAsync().AsTask();
        }
        // 正常退出由 Lifecycle 触发；先终止进程和监听，再给云端释放留出短暂时间。
        if (leaseCleanup is not null) try { leaseCleanup.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        if (!Dispatcher.CheckAccess() && _startup is { } startup)
            try { startup.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}
