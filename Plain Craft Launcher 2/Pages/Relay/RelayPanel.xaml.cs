using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Link.Relay;
using PCL.Core.UI.Dialogs;

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
    private readonly DispatcherTimer _realNameTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private RelayCaptcha? _captcha;
    private RelayRoomHost? _host;
    private RelayRoomClient? _guest;
    private string? _runningAllocationId;
    private IReadOnlyList<RelayGameServer> _games = [];
    private bool _roomsSupported = true;
    private bool _relayEnabled = true;
    private bool _exiting;
    private bool _initialized;
    private bool _banned;
    private bool _hasNodes;
    private bool _busy;
    private bool _refreshing;
    private bool _versionTooLow;
    private RelayMe? _lastMe;
    private bool _phonePrompted;
    // 服务端按来源 IP 判定不在中国大陆时为 true：登录与分配都禁用。
    private bool _regionBlocked;
    private bool _realNameCloudEnabled;
    private bool _realNameRequired;
    private bool _realNameVerified;
    private bool _realNamePolling;
    private string? _realNamePublicKey;
    private RelayRealNameRules? _realNameRules;
    private RelayServiceTerms? _relayTerms;
    private RelayServiceTerms? _confirmedTerms;
    private string? _termsReadToken;
    private string? _termsSessionToken;
    private DateTimeOffset _termsReadExpiresAt;
    private long _realNamePolicyRevision;
    private DialogViewModel? _termsDialog;
    private string _lastValidRealName = string.Empty;
    private bool _nameCompositionActive;
    private bool _nameValidationQueued;
    private bool _restoringRealName;
    private bool _loadingTerms;

    public RelayPanel()
    {
        InitializeComponent();
        foreach (var consent in new[] { CheckRealNameSensitiveConsent, CheckRealNameAlipayConsent })
        {
            consent.LabText.TextWrapping = TextWrapping.Wrap;
            consent.LabText.FontSize = 12;
            consent.LabText.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        _api = RelaySession.CreateApi(_session);
        DataObject.AddPastingHandler(TextRealName, TextRealName_Pasting);
        DataObject.AddPastingHandler(PasswordRealNameId, PasswordRealNameId_Pasting);
        TextCompositionManager.AddPreviewTextInputStartHandler(TextRealName, (_, _) => _nameCompositionActive = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(TextRealName, (_, _) => _nameCompositionActive = true);
        TextRealName.LostKeyboardFocus += (_, _) =>
        {
            _nameCompositionActive = false;
            QueueRealNameValidation();
        };
        // 这个面板常驻在联机页里，只有真正切到这个子页面时才算可见，
        // 所以初始化挂在可见性变化上，避免启动时就发请求。
        IsVisibleChanged += OnVisibleChanged;
        Application.Current.Exit += (_, _) => OnAppExit();

        // 停留在页面上时定期刷新：管理员改配额、封号都是即时生效的，
        // 不能等到用户下次切页才看到。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) =>
        {
            // 地区受限时不再刷新：既没有可用功能，也没必要每分钟去问一次属地
            if (!IsVisible || !_session.IsValid || _busy || _regionBlocked) return;
            await RefreshAsync().ConfigureAwait(true);
        };
        _realNameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _realNameTimer.Tick += async (_, _) => await RefreshRealNameStatusAsync().ConfigureAwait(true);
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _timer.Stop();
            _realNameTimer.Stop();
            _termsDialog?.Close(DialogResult.None);
            return;
        }

        _timer.Start();
        if (_host is null && _guest is null)
        {
            var stored = RelaySession.Load();
            if (_session.Token != stored.Token) ResetTermsConfirmation();
            _session.Token = stored.Token;
            _session.Email = stored.Email;
            _session.ExpiresAt = stored.ExpiresAt;
            _api.Token = _session.IsValid ? _session.Token : null;
        }
        try
        {
            if (!_initialized)
            {
                _initialized = true;
                // 请求期间给个可见反馈，别让页面看起来像卡住了（网络慢时尤其明显）
                LabEmpty.Text = "正在加载中继配置…";
                LabEmpty.Visibility = Visibility.Visible;
                await InitializeAsync().ConfigureAwait(true);
                return;
            }

            // 再次进来时先重新确认准入：管理员可能刚开关了地区限制，用户也可能换了网络，
            // 不能因为首次进入被拦过就一直停在受限态。服务端有缓存，这一步成本很低。
            var wasBlocked = _regionBlocked;
            await CheckAccessAsync().ConfigureAwait(true);
            if (_regionBlocked)
            {
                ShowRegionBlocked();
                return;
            }

            if (wasBlocked)
            {
                // 刚从受限态恢复：界面是被收起过的，需要按当前登录状态重建
                await EnterAvailableUiAsync().ConfigureAwait(true);
                return;
            }

            // 正常再进入：只刷新配额，不重取人机验证（否则用户填到一半的答案会失效）
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
        await CheckAccessAsync().ConfigureAwait(true);
        if (_regionBlocked)
        {
            // 地区受限时不再走登录流程：让用户白填邮箱和验证码没有意义
            ShowRegionBlocked();
            return;
        }

        await EnterAvailableUiAsync().ConfigureAwait(true);
    }

    /// <summary>
    ///     进入「可用」界面：已登录就显示配额并刷新，否则显示登录表单。
    ///     首次初始化与「地区限制解除后恢复」走同一套逻辑，避免两边状态不一致。
    /// </summary>
    private async Task EnterAvailableUiAsync()
    {
        if (_session.IsValid)
        {
            ShowLoggedIn();
            await RefreshAsync().ConfigureAwait(true);
            await PromptBindPhoneIfNeededAsync().ConfigureAwait(true);
            return;
        }

        // 本地没有可用令牌：曾经登录过的会话才算「已过期」，否则就是首次使用
        ShowLogin(_session.ExpiresAt != default);
        await RefreshCaptchaAsync().ConfigureAwait(true);
    }

    /// <summary>
    ///     进页面时向云端取一次准入状态：版本门槛与地区限制都在这一次请求里完成。
    ///     <para>
    ///         地区判定由服务端按请求来源 IP 完成（客户端不自己上报 IP，避免可伪造）。
    ///         这个请求必须限时：联机页是可操作页面，外部属地接口抖动不能把界面卡在这里。
    ///         超时按「不限制」处理，真正分配端口时服务端还会再校验一次。
    ///     </para>
    /// </summary>
    private async Task CheckAccessAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var status = await _api.GetStatusAsync(cts.Token).ConfigureAwait(true);
            if (_exiting) return;

            _roomsSupported = status.RoomCodes;
            _relayEnabled = status.RelayEnabled;
            _realNameCloudEnabled = status.RealNameEnabled;
            _realNameRequired = _realNameCloudEnabled;
            _realNamePublicKey = status.RealNamePublicKey;
            ApplyRealNamePolicy(status);
            if (!_relayEnabled) SetStatus("中继联机已由管理员关闭");
            if (!_roomsSupported) SetStatus("云端需要升级至 3.2.1 才能使用房间码");
            var required = status.RequiredVersion?.Trim();
            _versionTooLow = !string.IsNullOrEmpty(required) && required != Basics.VersionName;
            if (_versionTooLow) SetStatus("版本过低，需要更新");

            _regionBlocked = status.RegionCheck && !status.RegionAllowed;
            ApplyRegionState();
        }
        catch (OperationCanceledException)
        {
            // 超时：不改变判定，分配时由服务端兜底
            Common.Log("[Relay] 获取联机准入状态超时，跳过本地预检", Common.LogLevel.Debug);
        }
        catch (Exception ex)
        {
            // 状态接口失败不改变既有判定，分配时服务端兜底
            Common.Log("[Relay] 获取联机准入状态失败：" + ex.Message, Common.LogLevel.Debug);
        }
        finally
        {
            if (!_exiting && !_realNameCloudEnabled)
            {
                _realNameRequired = false;
                _realNameTimer.Stop();
                PanRealName.Visibility = Visibility.Collapsed;
                BtnRealName.Visibility = Visibility.Collapsed;
                ClearRealNameInput();
                ImgRealNameQr.Source = null;
            }
            UpdateButtons();
        }
    }

    /// <summary>把地区判定结果刷到界面上：受限时显示提示卡片，恢复后收起。</summary>
    private void ApplyRegionState()
    {
        // 两种方向都要处理：管理员中途关掉限制后，卡片不能留在页面上
        PanRegionBlocked.Visibility = _regionBlocked ? Visibility.Visible : Visibility.Collapsed;
        if (!_regionBlocked) return;

        SetStatus("当前可能位于非中国大陆地区，暂时无法使用此功能");
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

            ResetTermsConfirmation();
            _session.Token = result.Token;
            _session.Email = result.User.Email;
            _session.ExpiresAt = result.ExpiresAt;
            _session.Save();
            _api.Token = result.Token;

            ShowLoggedIn();
            ApplyQuota(result.Quota);
            SetStatus(result.IsNew ? "注册成功，已自动创建账号" : "登录成功");
            await RefreshAsync().ConfigureAwait(true);
            await PromptBindPhoneIfNeededAsync().ConfigureAwait(true);
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
        if (_busy) return;
        SetBusy(true, "正在退出登录…");
        var id = StopLocalRoom();
        var released = true;
        try
        {
            if (id is not null) await _api.ReleaseAsync(id, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception) { released = false; }
        try
        {
            _session.Clear();
            _api.Token = null;
            ListRooms.ItemsSource = null;
            LabEmpty.Text = "还没有创建的房间";
            LabEmpty.Visibility = Visibility.Visible;
            ShowLogin();
            await RefreshCaptchaAsync().ConfigureAwait(true);
            SetStatus(released ? "已退出登录" : "已退出登录，房间已停止，云端释放暂时失败");
        }
        finally { SetBusy(false); }
    }

    // ---------------------------------------------------------------- 手机号绑定

    private async void BtnBindPhone_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_session.IsValid) return;
        _phonePrompted = false;
        await PromptBindPhoneIfNeededAsync().ConfigureAwait(true);
    }

    /// <summary>
    ///     服务端启用「强制绑定手机号」且当前账号未绑定时，弹窗要求绑定。
    ///     每次登录主动弹一次，之后可通过账号卡上的「绑定手机号」按钮再次打开。
    /// </summary>
    private async Task PromptBindPhoneIfNeededAsync()
    {
        if (_lastMe is not { PhoneRequired: true } || _phonePrompted) return;
        _phonePrompted = true;
        try
        {
            var result = await DialogService.Instance.ShowAsync(new PhoneBindDialogRequest
            {
                Title = "绑定手机号",
                Content = "本服务已启用手机号绑定验证。请使用本人手机号完成验证，客户端仅显示脱敏后的绑定状态。",
                Buttons = DialogButtons.From(
                    DialogButton.Create("绑定手机号"),
                    DialogButton.Create("稍后再说")),
                ForceWait = true,
                GetCaptchaAsync = async () =>
                {
                    var captcha = await _api.GetCaptchaAsync().ConfigureAwait(false);
                    return (captcha.Id, captcha.Prompt);
                },
                SendSmsAsync = (phone, captchaId, answer) => _api.SendSmsCodeAsync(phone, captchaId, answer),
                BindAsync = (phone, code) => _api.BindPhoneAsync(phone, code)
            }).ConfigureAwait(true);

            if (result.Button == 1)
            {
                SetStatus("手机号绑定成功");
                await RefreshAsync().ConfigureAwait(true);
            }
            else
            {
                SetStatus("尚未绑定手机号，绑定后才能创建或加入房间");
            }
        }
        catch (Exception ex)
        {
            SetStatus("绑定手机号失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 数据

    private async Task RefreshAsync()
    {
        // 防重入：60 秒轮询、切页、手动操作都可能触发刷新，
        // 网络慢时上一次还没回来，再叠一次只会让请求互相拖慢。
        // 地区受限时也没有可刷新的内容。
        if (!_session.IsValid || _refreshing || _regionBlocked) return;
        _refreshing = true;
        try
        {
            await RefreshCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var token = _api.Token;
        try
        {
            // 账号信息与节点列表互不依赖，并行发出去：串行时网络慢就等于把两倍延迟叠起来，
            // 用户看到的是「切到联机页后一直没反应」。
            var meTask = _api.GetMeAsync(_lifetime.Token);
            var nodesTask = _api.GetNodesAsync(_lifetime.Token);
            // 无论哪一个失败，都观察两个任务，避免节点异常成为未处理任务。
            try { await Task.WhenAll(meTask, nodesTask).ConfigureAwait(true); }
            catch (Exception) { }

            var me = await meTask.ConfigureAwait(true);
            if (_exiting || _api.Token != token) return;
            _lastMe = me;
            ApplyMe(me);
            ApplyQuota(me.Quota);
            ApplyAllocations(me.Allocations);

            IReadOnlyList<RelayNode> nodes;
            try
            {
                nodes = await nodesTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                nodes = [];
                SetStatus("获取中继节点失败：" + ex.Message);
            }

            ApplyNodes(nodes);
            if (_realNameRequired && IsRealNamePending(me.User.RealNameStatus) && IsVisible)
                _ = RefreshRealNameStatusAsync();
        }
        catch (RelayApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            if (_exiting || _api.Token != token) return;
            StopLocalRoom();
            _session.Clear();
            _api.Token = null;
            ListRooms.ItemsSource = null;
            ShowLogin(true);
            await RefreshCaptchaAsync().ConfigureAwait(true);
            SetStatus("登录已过期，请重新登录");
        }
        catch (Exception ex)
        {
            if (_exiting || _api.Token != token) return;
            SetStatus("刷新失败：" + ex.Message);
        }
    }

    /// <summary>把账号状态（邮箱、用户组、封禁、手机号）刷到界面上。</summary>
    private void ApplyMe(RelayMe me)
    {
        _banned = me.IsBanned;
        _realNameRequired = _realNameCloudEnabled && me.User.RealNameEnabled != false;
        _realNameVerified = me.User.RealNameVerified || _realNameVerified;
        SetAuthState(_banned ? AuthState.Banned : AuthState.LoggedIn, me);
        if (_banned)
        {
            StopLocalRoom();
            SetStatus("账号已被封禁");
        }
        // 未绑定手机号且服务端强制绑定时，账号卡上给一个手动入口
        BtnBindPhone.Visibility = me.PhoneRequired ? Visibility.Visible : Visibility.Collapsed;
        ApplyRealNameUi(me.User.RealNameStatus, me.User.RealNameIdMasked);
        UpdateButtons();
    }

    private bool HasRealNamePolicy => _realNameRules?.IsValidConfiguration == true &&
        !string.IsNullOrWhiteSpace(_realNameRules.BirthDateMax) &&
        _relayTerms is { } terms && !string.IsNullOrWhiteSpace(terms.Title) &&
        !string.IsNullOrWhiteSpace(terms.Version) && !string.IsNullOrWhiteSpace(terms.Content);

    private void ApplyRealNamePolicy(RelayStatus status)
    {
        if (_realNameRules != status.RealNameRules || _relayTerms != status.RelayTerms)
            ResetTermsConfirmation("服务条款或实名认证规则已更新，请重新阅读并确认。");
        _realNameRules = status.RealNameRules;
        _relayTerms = status.RelayTerms;
        UpdateButtons();
    }

    private bool HasConfirmedTerms => HasRealNamePolicy && _confirmedTerms == _relayTerms &&
        _session.IsValid && !string.IsNullOrWhiteSpace(_termsReadToken) &&
        _termsSessionToken == _api.Token && _termsReadExpiresAt > DateTimeOffset.UtcNow;

    private void ResetTermsConfirmation(string? reason = null)
    {
        _confirmedTerms = null;
        _termsReadToken = null;
        _termsSessionToken = null;
        _termsReadExpiresAt = default;
        _realNamePolicyRevision++;
        _termsDialog?.Close(DialogResult.None);
        if (CheckRealNameSensitiveConsent is not null) CheckRealNameSensitiveConsent.Checked = false;
        if (CheckRealNameAlipayConsent is not null) CheckRealNameAlipayConsent.Checked = false;
        if (LabRealNameTermsStatus is not null)
            LabRealNameTermsStatus.Text = reason ?? "请先阅读并确认条款，再分别确认以下授权事项。";
    }

    private async void BtnRelayTerms_Click(object sender, EventArgs e)
    {
        if (_loadingTerms || _exiting || !IsVisible) return;
        _loadingTerms = true;
        UpdateButtons();
        var sessionToken = _api.Token;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var status = await _api.GetStatusAsync(cts.Token).ConfigureAwait(true);
            if (_exiting || !IsVisible || _api.Token != sessionToken) return;
            ApplyRealNamePolicy(status);
            if (_relayTerms is not { } terms || string.IsNullOrWhiteSpace(terms.Title) ||
                string.IsNullOrWhiteSpace(terms.Version) || string.IsNullOrWhiteSpace(terms.Content))
            {
                SetStatus("云端尚未提供中继服务条款，请联系服务管理员。");
                return;
            }
            var requiresConfirmation = _session.IsValid && _realNameRequired && !IsRealNameVerified;
            RelayRealNameTermsRead? ticket = null;
            if (requiresConfirmation)
            {
                ResetTermsConfirmation();
                if (!HasRealNamePolicy)
                {
                    SetStatus("暂未取得完整的实名认证规则，请联系服务管理员确认云端配置。");
                    return;
                }
                ticket = await _api.OpenRealNameTermsAsync(terms.Version, cts.Token).ConfigureAwait(true);
                if (_exiting || !IsVisible || _api.Token != sessionToken) return;
                if (ticket.Terms != terms || ticket.Terms != _relayTerms ||
                    string.IsNullOrWhiteSpace(ticket.ReadToken) || ticket.MinimumReadSeconds is < 10 or > 300 ||
                    ticket.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(ticket.MinimumReadSeconds))
                {
                    SetStatus("条款内容或阅读确认凭据已变更，请重新打开条款。");
                    return;
                }
            }
            var policyRevision = _realNamePolicyRevision;
            var rules = _realNameRules;
            bool CanConfirm() => !_exiting && IsVisible && _session.IsValid && _api.Token == sessionToken &&
                HasRealNamePolicy && _relayTerms == terms && _realNameRules == rules &&
                _realNamePolicyRevision == policyRevision && ticket is not null && ticket.ExpiresAt > DateTimeOffset.UtcNow;
            var dialog = DialogService.Instance.Show(new TextDialogRequest
            {
                Title = terms.Title,
                Content = $"条款版本：{terms.Version}\n\n{terms.Content}",
                Buttons = requiresConfirmation
                    ? DialogButtons.From("已阅读并同意", "取消")
                    : DialogButtons.From("关闭"),
                ReadingConfirmation = requiresConfirmation ? new DialogReadingConfirmation
                {
                    MinimumReadSeconds = ticket!.MinimumReadSeconds,
                    RequireScrollEnd = true,
                    CanConfirm = CanConfirm
                } : null
            });
            _termsDialog = dialog;
            var result = await dialog.WaitForCloseAsync().ConfigureAwait(true);
            if (requiresConfirmation && result.IsConfirmed && CanConfirm())
            {
                _confirmedTerms = terms;
                _termsReadToken = ticket!.ReadToken;
                _termsReadExpiresAt = ticket.ExpiresAt;
                _termsSessionToken = sessionToken;
                LabRealNameTermsStatus.Text = "已确认当前版本服务条款，请分别确认以下授权事项。";
            }
        }
        catch (OperationCanceledException) when (_exiting) { }
        catch (OperationCanceledException) { SetStatus("获取中继服务条款超时，请稍后重试。"); }
        catch (Exception ex) { if (!_exiting) SetStatus("无法获取中继服务条款：" + ex.Message); }
        finally
        {
            _termsDialog = null;
            _loadingTerms = false;
            if (!_exiting) UpdateButtons();
        }
    }

    private void TextRealName_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        _nameCompositionActive = false;
        e.Handled = !RelayApi.IsAllowedRealNameInput(e.Text);
        QueueRealNameValidation();
    }

    private void TextRealName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_restoringRealName) return;
        if (!_nameCompositionActive && sender is TextBox textBox && RelayApi.IsAllowedRealNameInput(textBox.Text))
        {
            _lastValidRealName = textBox.Text;
            return;
        }
        QueueRealNameValidation();
    }

    private void QueueRealNameValidation()
    {
        if (_nameValidationQueued) return;
        _nameValidationQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _nameValidationQueued = false;
            if (_nameCompositionActive || _exiting) return;
            if (RelayApi.IsAllowedRealNameInput(TextRealName.Text))
            {
                _lastValidRealName = TextRealName.Text;
                return;
            }
            _restoringRealName = true;
            try
            {
                TextRealName.Text = _lastValidRealName;
                TextRealName.CaretIndex = TextRealName.Text.Length;
                SetStatus("姓名仅允许填写中文汉字，长度为 2 至 6 个字。");
            }
            finally { _restoringRealName = false; }
        }));
    }

    private void TextRealName_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
        var candidate = text is null ? null : TextRealName.Text.Remove(TextRealName.SelectionStart, TextRealName.SelectionLength)
            .Insert(TextRealName.SelectionStart, text);
        if (candidate is not null && RelayApi.IsAllowedRealNameInput(candidate)) return;
        e.CancelCommand();
        SetStatus("姓名仅允许填写中文汉字，长度为 2 至 6 个字。");
    }

    private static bool IsIdentityInputCharacter(char value) => value is >= '0' and <= '9' or 'X' or 'x';

    private void PasswordRealNameId_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Any(value => !IsIdentityInputCharacter(value)))
        {
            e.Handled = true;
            SetStatus("身份证号仅允许填写数字，末位校验码可填写 X。");
            return;
        }
        if (e.Text.Any(value => value is 'X' or 'x'))
        {
            using var current = PasswordRealNameId.SecurePassword;
            if (current.Length < 17)
            {
                e.Handled = true;
                SetStatus("X 仅可作为居民身份证号的第 18 位校验码。");
                return;
            }
        }
        if (!e.Text.Contains('x')) return;
        e.Handled = true;
        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, PasswordRealNameId, e.Text.ToUpperInvariant()));
    }

    private void PasswordRealNameId_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
        if (text is null || text.Length > 18 || text.Any(value => !IsIdentityInputCharacter(value)))
        {
            e.CancelCommand();
            SetStatus("请粘贴有效的居民身份证号，限 18 位，末位可为 X。");
            return;
        }
        e.DataObject = new DataObject(DataFormats.UnicodeText, text.ToUpperInvariant());
    }

    private void PasswordRealNameId_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        using var value = box.SecurePassword;
        var chars = new char[value.Length];
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(value);
            Marshal.Copy(pointer, chars, 0, chars.Length);
            for (var index = 0; index < chars.Length; index++)
            {
                if (chars[index] is >= '0' and <= '9' || index == 17 && chars[index] == 'X') continue;
                box.Clear();
                if (LabStatus is not null) SetStatus("身份证号前 17 位应为数字，末位应为数字或 X，请重新填写。");
                break;
            }
        }
        finally
        {
            Array.Clear(chars);
            if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    private void ClearRealNameInput()
    {
        _nameCompositionActive = false;
        _lastValidRealName = string.Empty;
        TextRealName.Text = string.Empty;
        PasswordRealNameId.Clear();
        ResetTermsConfirmation();
    }

    private void ApplyRealNameUi(string? status, string? maskedId)
    {
        if (!_realNameRequired)
        {
            BtnRealName.Visibility = Visibility.Collapsed;
            PanRealName.Visibility = Visibility.Collapsed;
            _realNameTimer.Stop();
            return;
        }

        BtnRealName.Visibility = Visibility.Visible;
        BtnRealName.Text = _realNameVerified ? "认证已完成" : "实名认证";
        if (_realNameVerified)
        {
            BtnRealName.Visibility = Visibility.Collapsed;
            PanRealName.Visibility = Visibility.Visible;
            _realNameTimer.Stop();
            PanRealNameForm.Visibility = Visibility.Collapsed;
            ClearRealNameInput();
            ImgRealNameQr.Source = null;
            ImgRealNameQr.Visibility = Visibility.Collapsed;
            LabRealNameStatus.Text = string.IsNullOrWhiteSpace(maskedId)
                ? "实名认证已完成。经认证的身份资料不可修改，客户端仅提供认证状态及脱敏信息。"
                : $"实名认证已完成（身份证号：{maskedId}）。经认证的身份资料不可修改。";
        }
        else if (IsRealNamePending(status))
        {
            LabRealNameStatus.Text = "实名认证申请已受理，请使用支付宝扫描下方二维码完成身份核验。";
            PanRealNameForm.Visibility = Visibility.Collapsed;
            if (ImgRealNameQr.Source is not null) ImgRealNameQr.Visibility = Visibility.Visible;
            _realNameTimer.Start();
        }
        else
        {
            LabRealNameStatus.Text = string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase)
                ? "本次身份核验二维码已失效，请核对本人资料后重新提交认证申请。"
                : "使用中继联机服务前，请完成实名认证。提交前请阅读中继服务条款，了解资料用途及处理方式，并确认授权。";
            PanRealNameForm.Visibility = Visibility.Visible;
            ImgRealNameQr.Visibility = Visibility.Collapsed;
            _realNameTimer.Stop();
        }
    }

    private void BtnRealName_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_realNameRequired) return;
        PanRealName.Visibility = PanRealName.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (PanRealName.Visibility == Visibility.Visible && IsRealNamePending(_lastMe?.User.RealNameStatus))
            _ = RefreshRealNameStatusAsync();
    }

    private async void BtnRealNameSubmit_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _exiting || !_session.IsValid || !_realNameRequired || IsRealNameVerified) return;
        if (!HasRealNamePolicy)
        {
            SetStatus("暂未取得有效的实名校验规则或服务条款，请联系服务管理员。");
            return;
        }
        if (!HasConfirmedTerms)
        {
            SetStatus("请先完整阅读中继服务条款，并在阅读窗口确认后提交。");
            return;
        }
        if (CheckRealNameSensitiveConsent.Checked != true || CheckRealNameAlipayConsent.Checked != true)
        {
            SetStatus("请分别确认敏感个人信息处理授权及向支付宝实名认证服务提供方提供资料的授权。");
            return;
        }
        var acceptedTerms = _confirmedTerms!;
        var sessionToken = _api.Token;
        var name = TextRealName.Text.Trim();
        using var secureId = PasswordRealNameId.SecurePassword;
        if (!RelayApi.IsValidRealName(name))
        {
            SetStatus("请填写本人真实姓名，仅允许 2 至 6 个中文汉字。");
            return;
        }
        if (!ValidateRealNameIdentity(secureId)) return;
        if (string.IsNullOrWhiteSpace(_realNamePublicKey))
        {
            SetStatus("云端未提供实名认证所需的加密公钥，请联系服务管理员。");
            return;
        }

        try
        {
            SetBusy(true, "正在核对实名认证规则及授权条款…");
            using var policyTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            policyTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            var latestStatus = await _api.GetStatusAsync(policyTimeout.Token).ConfigureAwait(true);
            if (_exiting || _api.Token != sessionToken) return;
            ApplyRealNamePolicy(latestStatus);
            _realNamePublicKey = latestStatus.RealNamePublicKey;
            if (!latestStatus.RealNameEnabled || !HasConfirmedTerms || _confirmedTerms != acceptedTerms ||
                CheckRealNameSensitiveConsent.Checked != true || CheckRealNameAlipayConsent.Checked != true)
            {
                SetStatus("实名认证配置或服务条款已更新，请重新阅读并确认授权后提交。");
                return;
            }
            if (!ValidateRealNameIdentity(secureId)) return;
            if (string.IsNullOrWhiteSpace(_realNamePublicKey))
            {
                SetStatus("云端未提供有效的实名认证加密公钥，请联系服务管理员。");
                return;
            }
            SetStatus("正在提交实名认证申请…");
            var submission = _api.StartRealNameAsync(_realNamePublicKey, name, secureId, acceptedTerms.Version,
                _termsReadToken!, CheckRealNameSensitiveConsent.Checked == true,
                CheckRealNameAlipayConsent.Checked == true, _lifetime.Token);
            secureId.Dispose();
            name = string.Empty;
            ClearRealNameInput();
            var state = await submission.ConfigureAwait(true);
            if (_exiting || !IsVisible || _api.Token != sessionToken) return;
            PanRealName.Visibility = Visibility.Visible;
            await ApplyRealNameStateAsync(state).ConfigureAwait(true);
            if (IsVerifiedStatus(state.Status))
            {
                await RefreshAsync().ConfigureAwait(true);
                SetStatus("实名认证已完成，可使用中继联机服务。");
            }
            else SetStatus("实名认证申请已提交，请使用支付宝扫码完成身份核验。");
        }
        catch (OperationCanceledException) when (_exiting) { }
        catch (Exception ex)
        {
            if (!_exiting) SetStatus("提交实名认证失败：" + ex.Message);
        }
        finally
        {
            ClearRealNameInput();
            if (!_exiting) SetBusy(false);
        }
    }

    private void CheckRealNameConsent_Changed(object sender, bool user)
    {
        // XAML 初始化期间，后续命名控件可能尚未创建。
        if (BtnRealNameSubmit is null || CheckRealNameSensitiveConsent is null || CheckRealNameAlipayConsent is null) return;
        if (!HasConfirmedTerms)
        {
            CheckRealNameSensitiveConsent.Checked = false;
            CheckRealNameAlipayConsent.Checked = false;
        }
        UpdateButtons();
    }

    private bool ValidateRealNameIdentity(System.Security.SecureString identity)
    {
        if (_realNameRules is not { } rules || !RelayApi.IsValidIdentityNumber(identity, rules with { MinimumAge = 0 }))
        {
            SetStatus("居民身份证号码校验未通过，请核对号码、出生日期及末位校验码。");
            return false;
        }
        if (RelayApi.IsRealNameMinimumAgeSatisfied(identity, rules)) return true;
        SetStatus($"根据本服务的准入规则，实名认证申请人须年满 {rules.MinimumAge} 周岁。您目前未达到申请年龄要求。");
        return false;
    }

    private async Task RefreshRealNameStatusAsync()
    {
        if (_realNamePolling || _exiting || !IsVisible || !_session.IsValid || !_realNameRequired ||
            IsRealNameVerified || _busy) return;
        _realNamePolling = true;
        try
        {
            var state = await _api.GetRealNameStatusAsync(_lifetime.Token).ConfigureAwait(true);
            if (_exiting || !IsVisible) return;
            await ApplyRealNameStateAsync(state).ConfigureAwait(true);
            if (IsVerifiedStatus(state.Status))
            {
                _realNameVerified = true;
                await RefreshAsync().ConfigureAwait(true);
                if (!_exiting) SetStatus("实名认证已完成，可使用中继联机服务。");
            }
        }
        catch (OperationCanceledException) when (_exiting) { }
        catch (Exception ex)
        {
            if (!_exiting && IsVisible) LabRealNameStatus.Text = "查询认证状态失败：" + ex.Message;
        }
        finally { _realNamePolling = false; }
    }

    private async Task ApplyRealNameStateAsync(RelayRealNameState state)
    {
        if (_exiting || !IsVisible) return;
        if (IsVerifiedStatus(state.Status))
        {
            _realNameVerified = true;
            _realNameTimer.Stop();
            BtnRealName.Visibility = Visibility.Collapsed;
            PanRealName.Visibility = Visibility.Visible;
            LabRealNameStatus.Text = string.IsNullOrWhiteSpace(state.MaskedId)
                ? "实名认证已完成。经认证的身份资料不可修改，客户端仅提供认证状态及脱敏信息。"
                : $"实名认证已完成（身份证号：{state.MaskedId}）。经认证的身份资料不可修改。";
            ClearRealNameInput();
            PanRealNameForm.Visibility = Visibility.Collapsed;
            ImgRealNameQr.Source = null;
            ImgRealNameQr.Visibility = Visibility.Collapsed;
            UpdateButtons();
            return;
        }

        if (IsRealNamePending(state.Status) || !string.IsNullOrWhiteSpace(state.QrPng))
        {
            LabRealNameStatus.Text = state.ExpiresAt is DateTimeOffset expires
                ? $"实名认证申请已受理，请使用支付宝扫码完成身份核验。二维码有效期至 {expires.LocalDateTime:yyyy-MM-dd HH:mm}。"
                : "实名认证申请已受理，请使用支付宝扫描下方二维码完成身份核验。";
            PanRealNameForm.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(state.QrPng))
            {
                try
                {
                    var base64 = state.QrPng.Contains(',') ? state.QrPng[(state.QrPng.IndexOf(',') + 1)..] : state.QrPng;
                    var bytes = Convert.FromBase64String(base64);
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    if (_exiting || !IsVisible) return;
                    ImgRealNameQr.Source = bitmap;
                    ImgRealNameQr.Visibility = Visibility.Visible;
                }
                catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException)
                {
                    ImgRealNameQr.Source = null;
                    ImgRealNameQr.Visibility = Visibility.Collapsed;
                    LabRealNameStatus.Text = "云端返回的二维码无效，请稍后刷新认证状态。";
                }
            }
            else ImgRealNameQr.Visibility = Visibility.Collapsed;
            _realNameTimer.Start();
            return;
        }

        _realNameTimer.Stop();
        ImgRealNameQr.Source = null;
        ImgRealNameQr.Visibility = Visibility.Collapsed;
        PanRealNameForm.Visibility = Visibility.Visible;
        LabRealNameStatus.Text = string.Equals(state.Status, "expired", StringComparison.OrdinalIgnoreCase)
            ? "本次身份核验二维码已失效，请核对本人资料后重新提交认证申请。"
            : "尚未完成实名认证，请核对本人资料并确认授权后提交认证申请。";
    }

    private static bool IsVerifiedStatus(string? status) =>
        status is not null && (status.Equals("verified", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("passed", StringComparison.OrdinalIgnoreCase));

    private static bool IsRealNamePending(string? status) =>
        status is not null && (status.Equals("pending", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("waiting", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("processing", StringComparison.OrdinalIgnoreCase));

    private void ApplyNodes(IReadOnlyList<RelayNode> nodes)
    {
        var selected = (ComboNodes.SelectedItem as RelayNode)?.Id;
        var compatible = nodes.Where(node => node.RoomCodes).ToArray();
        if (compatible.Length == 0)
        {
            ComboNodes.ItemsSource = new[] { new RelayNode("", nodes.Count > 0 ? "节点需要升级至 3.2.1" : "暂无可用节点", "", "", 0, 0) };
            ComboNodes.SelectedIndex = 0;
            _hasNodes = false;
            LabEmpty.Text = !_roomsSupported ? "请先将云端升级至 3.2.1" :
                nodes.Count > 0 ? "请先将中继节点升级至 3.2.1" : "服务端还没有可用的中继节点";
            LabEmpty.Visibility = Visibility.Visible;
            UpdateButtons();
            return;
        }

        _hasNodes = true;
        ComboNodes.ItemsSource = compatible;
        ComboNodes.SelectedItem = compatible.FirstOrDefault(n => n.Id == selected) ?? compatible[0];
        LabEmpty.Text = "还没有创建的房间";
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
        var items = list.Select(a => new RoomItem(a)).ToList();
        ListRooms.ItemsSource = items;
        LabEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 房间

    private async Task<bool> CanEnterRoomAsync()
    {
        if (_busy || _exiting) return false;
        if (!_relayEnabled) { SetStatus("中继联机已由管理员关闭"); return false; }
        if (_regionBlocked)
        {
            SetStatus("当前可能位于非中国大陆地区，暂时无法使用此功能");
            return false;
        }
        if (_versionTooLow)
        {
            SetStatus("版本过低，需要更新");
            return false;
        }
        if (!_session.IsValid)
        {
            SetStatus("请先登录");
            return false;
        }
        if (_banned)
        {
            SetStatus("账号已被封禁，无法创建或加入房间");
            return false;
        }
        if (_realNameRequired && !IsRealNameVerified)
        {
            PanRealName.Visibility = Visibility.Visible;
            SetStatus("请先完成实名认证后再使用中继联机");
            return false;
        }
        if (!_roomsSupported)
        {
            SetStatus("云端需要升级至 3.2.1 才能使用房间码");
            return false;
        }
        if (_host is not null || _guest is not null)
        {
            SetStatus("请先关闭或退出当前房间");
            return false;
        }
        if (_lastMe is { PhoneRequired: true })
        {
            _phonePrompted = false;
            await PromptBindPhoneIfNeededAsync().ConfigureAwait(true);
            return false;
        }
        return true;
    }

    private async Task ScanGamesAsync()
    {
        _games = await RelayGameDiscovery.ScanAsync(_lifetime.Token).ConfigureAwait(true);
        ComboGames.ItemsSource = _games;
        ComboGames.Visibility = _games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ComboGames.SelectedIndex = _games.Count == 1 ? 0 : -1;
        LabScanHint.Text = _games.Count switch
        {
            0 => "未发现可用游戏。请先进入世界，在暂停菜单中选择“对局域网开放”，然后重新扫描。",
            1 => "已验证游戏：" + _games[0],
            _ => $"发现 {_games.Count} 个已验证的游戏，请选择要分享的游戏后点击创建房间。"
        };
    }

    private async void BtnScan_Click(object sender, MouseButtonEventArgs e)
    {
        if (!await CanEnterRoomAsync().ConfigureAwait(true)) return;
        try
        {
            SetBusy(true, "正在扫描并验证本机游戏…");
            await ScanGamesAsync().ConfigureAwait(true);
            SetStatus(_games.Count > 0 ? "游戏扫描完成" : "未发现已开放的游戏房间");
        }
        catch (OperationCanceledException) when (_exiting) { }
        catch (Exception ex) { SetStatus("扫描失败：" + ex.Message); }
        finally { SetBusy(false); }
    }

    private async void BtnAllocate_Click(object sender, MouseButtonEventArgs e)
    {
        if (!await CanEnterRoomAsync().ConfigureAwait(true)) return;
        if (ComboNodes.SelectedItem is not RelayNode { RoomCodes: true } node || string.IsNullOrEmpty(node.Id))
        {
            SetStatus("请选择节点");
            return;
        }

        RelayAllocationResult? result = null;
        RelayRoomHost? host = null;
        try
        {
            SetBusy(true, "正在扫描并验证本机游戏…");
            if (_games.Count == 0) await ScanGamesAsync().ConfigureAwait(true);
            if (ComboGames.SelectedItem is not RelayGameServer game)
            {
                SetStatus(_games.Count == 0 ? "未发现游戏，请先在游戏中打开局域网" : "请选择要分享的游戏");
                return;
            }
            var verified = await MinecraftServerProbe.ProbeAsync(game.Endpoint, TimeSpan.FromSeconds(3), _lifetime.Token)
                .ConfigureAwait(true);
            if (verified is null)
            {
                _games = [];
                ComboGames.ItemsSource = null;
                ComboGames.Visibility = Visibility.Collapsed;
                LabScanHint.Text = "游戏房间已关闭，请重新打开局域网后扫描。";
                SetStatus("游戏验证失败，请重新扫描");
                return;
            }
            SetStatus("正在向云端申请房间码…");
            result = await _api.CreateRoomAsync(node.Id, game.Name, verified.Version, _lifetime.Token).ConfigureAwait(true);
            _runningAllocationId = result.Allocation.Id;
            host = new RelayRoomHost(_api, result, game);
            _host = host;
            AttachHostEvents(host);
            SetStatus("正在建立房间连接…");
            await host.StartAsync(_lifetime.Token).ConfigureAwait(true);
            if (_host != host || _exiting) return;
            BtnStop.Text = "关闭房间";
            BtnStop.Visibility = Visibility.Visible;
            ApplyQuota(result.Quota);
            ApplyAllocations((_lastMe?.Allocations ?? []).Where(a => a.Id != result.Allocation.Id)
                .Append(result.Allocation with { RoomReady = true }).ToArray());
            await RefreshAsync().ConfigureAwait(true);
            if (_host != host || _exiting) return;
            SetStatus("房间已创建：" + result.Allocation.RoomCode + "，复制房间码分享给朋友即可");
        }
        catch (Exception ex)
        {
            host?.Dispose();
            if (_host == host) StopLocalRoom();
            if (result is not null && !_exiting)
            {
                try { await _api.ReleaseAsync(result.Allocation.Id, _lifetime.Token).ConfigureAwait(true); }
                catch (Exception) { }
            }
            if (!_exiting) await HandleRoomErrorAsync(ex).ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnRelease_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: RoomItem item }) return;

        try
        {
            SetBusy(true, "正在关闭房间…");
            if (_runningAllocationId == item.Allocation.Id) StopLocalRoom();
            await _api.ReleaseAsync(item.Allocation.Id, _lifetime.Token).ConfigureAwait(true);
            SetStatus("房间已关闭，房间码已失效");
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
        if (sender is not FrameworkElement { Tag: RoomItem { CanCopy: true } item }) return;
        try
        {
            Clipboard.SetText(item.RoomCode);
            SetStatus("已复制房间码：" + item.RoomCode);
        }
        catch (Exception)
        {
            SetStatus("房间码复制失败，请稍后重试");
        }
    }

    private async void BtnJoin_Click(object sender, MouseButtonEventArgs e)
    {
        if (!await CanEnterRoomAsync().ConfigureAwait(true)) return;
        if (!RelayRoomCode.TryNormalize(TextRoomCode.Text, out var code))
        {
            SetStatus("房间码格式不正确，请粘贴完整的五组房间码");
            return;
        }
        RelayRoomClient? guest = null;
        try
        {
            SetBusy(true, "正在查询房间码…");
            TextRoomCode.Text = code;
            var room = await _api.JoinRoomAsync(code, _lifetime.Token).ConfigureAwait(true);
            guest = new RelayRoomClient(_api, room);
            _guest = guest;
            AttachGuestEvents(guest);
            SetStatus("正在验证房间连接…");
            await guest.StartAsync(_lifetime.Token).ConfigureAwait(true);
            if (_guest != guest || _exiting) return;
            LabJoinedName.Text = room.Name + " · " + room.GameVersion + " · " + room.NodeName;
            LabJoinedCode.Text = room.Code;
            PanJoinedRoom.Visibility = Visibility.Visible;
            BtnStop.Text = "退出房间";
            BtnStop.Visibility = Visibility.Visible;
            SetStatus("已加入房间，打开游戏的多人游戏列表即可进入");
        }
        catch (Exception ex)
        {
            guest?.Dispose();
            if (_guest == guest) StopLocalRoom();
            if (!_exiting) await HandleRoomErrorAsync(ex).ConfigureAwait(true);
        }
        finally { SetBusy(false); }
    }

    private void AttachHostEvents(RelayRoomHost host)
    {
        host.StatusChanged += text => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_exiting && _host == host && !_busy) SetStatus(text);
        }));
        host.Closed += reason => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_exiting || _host != host) return;
            StopLocalRoom();
            await RefreshAsync().ConfigureAwait(true);
            SetStatus(reason);
        }));
    }

    private void AttachGuestEvents(RelayRoomClient guest)
    {
        guest.StatusChanged += text => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_exiting && _guest == guest && !_busy) SetStatus(text);
        }));
        guest.Closed += reason => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting || _guest != guest) return;
            StopLocalRoom();
            SetStatus(reason);
        }));
    }

    private async Task HandleRoomErrorAsync(Exception error)
    {
        if (error is RelayApiException apiError && IsRegionBlocked(apiError))
        {
            _regionBlocked = true;
            ApplyRegionState();
        }
        else
        {
            await RefreshAsync().ConfigureAwait(true);
            SetStatus(error.Message);
            if (_lastMe is { PhoneRequired: true })
            {
                _phonePrompted = false;
                await PromptBindPhoneIfNeededAsync().ConfigureAwait(true);
            }
        }
    }

    private async void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        await StopAndReleaseAsync().ConfigureAwait(true);
    }

    /// <summary>房主关闭时释放云端分配；访客退出只关闭本机入口。</summary>
    private async Task StopAndReleaseAsync()
    {
        var id = StopLocalRoom();
        if (id is null)
        {
            SetStatus("已退出房间");
            return;
        }

        try
        {
            SetBusy(true, "正在关闭房间…");
            await _api.ReleaseAsync(id, _lifetime.Token).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            SetStatus("房间已关闭，房间码已失效");
        }
        catch (Exception ex)
        {
            SetStatus("房间已停止，云端释放失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>退出也视为停止：尽力释放占用的端口，失败留给服务端 TTL 兜底，不拦退出。</summary>
    private void OnAppExit()
    {
        _exiting = true;
        _termsDialog?.Close(DialogResult.None);
        ResetTermsConfirmation();
        _timer.Stop();
        _realNameTimer.Stop();
        _lifetime.Cancel();
        var id = _runningAllocationId;
        _host?.Dispose();
        _guest?.Dispose();
        _host = null;
        _guest = null;
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

    private string? StopLocalRoom()
    {
        var id = _runningAllocationId;
        _host?.Dispose();
        _guest?.Dispose();
        _host = null;
        _guest = null;
        _runningAllocationId = null;
        BtnStop.Visibility = Visibility.Collapsed;
        PanJoinedRoom.Visibility = Visibility.Collapsed;
        UpdateButtons();
        return id;
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
                LabAccountSub.Text = "登录后即可创建或加入房间";
                break;

            case AuthState.Expired:
                DotAuth.Fill = AuthDotWarn;
                LabAccount.Text = "登录已过期";
                LabAccountSub.Text = "会话令牌已失效，请重新登录后使用房间";
                break;

            case AuthState.Banned:
                DotAuth.Fill = AuthDotBad;
                LabAccount.Text = "账号已封禁";
                LabAccountSub.Text = string.IsNullOrWhiteSpace(email)
                    ? "无法创建或加入房间，请联系管理员"
                    : $"{email} · 无法创建或加入房间，请联系管理员";
                break;

            default:
                DotAuth.Fill = AuthDotOk;
                LabAccount.Text = "已登录";

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(email)) parts.Add(email);
                if (me?.User.PhoneBound == true && !string.IsNullOrWhiteSpace(me.User.Phone))
                    parts.Add("+86 " + MaskPhone(me.User.Phone));
                else if (me?.ForceBindPhone == true)
                    parts.Add("未绑定手机号");
                if (!string.IsNullOrWhiteSpace(me?.Quota.GroupName)) parts.Add("用户组 " + me!.Quota.GroupName);
                if (me?.Quota.CustomQuota == true) parts.Add("单独配额");
                if (_session.ExpiresAt != default)
                    parts.Add("会话有效期至 " + _session.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
                LabAccountSub.Text = parts.Count == 0 ? "可以创建或加入房间" : string.Join(" · ", parts);
                break;
        }
    }

    private void ShowLogin(bool expired = false)
    {
        _realNameRequired = false;
        _realNameVerified = false;
        _realNameTimer.Stop();
        BtnRealName.Visibility = Visibility.Collapsed;
        PanRealName.Visibility = Visibility.Collapsed;
        ImgRealNameQr.Source = null;
        ClearRealNameInput();
        PanLogin.Visibility = Visibility.Visible;
        PanQuota.Visibility = Visibility.Collapsed;
        BtnLogout.Visibility = Visibility.Collapsed;
        BtnBindPhone.Visibility = Visibility.Collapsed;
        SetAuthState(expired ? AuthState.Expired : AuthState.LoggedOut);
        BtnStop.Visibility = Visibility.Collapsed;
        _banned = false;
        _lastMe = null;
        _phonePrompted = false;
        ListRooms.ItemsSource = null;
        LabEmpty.Text = "登录后即可创建或加入房间";
        LabEmpty.Visibility = Visibility.Visible;
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

    /// <summary>
    ///     地区受限时的界面：收起登录与配额区，只保留提示。
    ///     <para>
    ///         已经建立的隧道不主动断开——那会把正在进行中的游戏直接打断，
    ///         这里只保证不再新建；停止按钮保留，让用户自己决定何时停。
    ///     </para>
    /// </summary>
    private void ShowRegionBlocked()
    {
        _realNameTimer.Stop();
        PanLogin.Visibility = Visibility.Collapsed;
        PanQuota.Visibility = Visibility.Collapsed;
        BtnLogout.Visibility = Visibility.Collapsed;
        BtnBindPhone.Visibility = Visibility.Collapsed;
        BtnStop.Visibility = _host is null && _guest is null ? Visibility.Collapsed : Visibility.Visible;
        ListRooms.ItemsSource = null;
        LabEmpty.Visibility = Visibility.Collapsed;
        PanRegionBlocked.Visibility = Visibility.Visible;
        _lastMe = null;
        _phonePrompted = true; // 地区受限时不再弹绑定手机号
        UpdateButtons();
    }

    /// <summary>封禁、没有节点、版本过低、地区受限、正在忙碌时，分配按钮都不能点。</summary>
    private void UpdateButtons()
    {
        if (_confirmedTerms is not null && !HasConfirmedTerms)
            ResetTermsConfirmation("条款阅读确认已失效，请重新阅读并确认后提交。");
        var available = _session.IsValid && _relayEnabled && !_busy && !_banned && !_versionTooLow && !_regionBlocked && _roomsSupported &&
            (!_realNameRequired || IsRealNameVerified) &&
            _host is null && _guest is null && !_exiting;
        BtnAllocate.IsEnabled = available && _hasNodes;
        BtnScan.IsEnabled = available;
        BtnJoin.IsEnabled = available;
        ComboNodes.IsEnabled = available && _hasNodes;
        ComboGames.IsEnabled = available;
        TextRoomCode.IsEnabled = available;
        BtnStop.IsEnabled = !_busy;
        BtnLogout.IsEnabled = !_busy;
        BtnRealName.IsEnabled = !_busy;
        var canSubmitRealName = _session.IsValid && !_busy && !_exiting && _realNameRequired && !IsRealNameVerified;
        TextRealName.IsEnabled = canSubmitRealName && HasRealNamePolicy;
        PasswordRealNameId.IsEnabled = canSubmitRealName && HasRealNamePolicy;
        CheckRealNameSensitiveConsent.IsEnabled = canSubmitRealName && !_loadingTerms && HasConfirmedTerms;
        CheckRealNameAlipayConsent.IsEnabled = canSubmitRealName && !_loadingTerms && HasConfirmedTerms;
        BtnRealNameTerms.IsEnabled = !_busy && !_loadingTerms && !_exiting;
        BtnRelayTerms.IsEnabled = !_busy && !_loadingTerms && !_exiting;
        BtnRealNameSubmit.IsEnabled = canSubmitRealName && !_loadingTerms && HasConfirmedTerms &&
            CheckRealNameSensitiveConsent.Checked == true && CheckRealNameAlipayConsent.Checked == true;
        // 地区受限时连登录都禁用，避免用户以为登录之后就能用
        BtnRefreshCaptcha.IsEnabled = !_busy && !_regionBlocked;
        BtnSendCode.IsEnabled = !_busy && !_regionBlocked;
        BtnLogin.IsEnabled = !_busy && !_regionBlocked;
    }

    private void SetStatus(string text) => LabStatus.Text = text;

    private bool IsRealNameVerified => _realNameVerified || _lastMe?.User.RealNameVerified == true;

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

    /// <summary>
    ///     手机号脱敏：账号卡上只显示前 3 位与后 4 位。
    ///     完整号码在客户端没有任何用途，少显示一位就少一分泄露风险。
    /// </summary>
    private static string MaskPhone(string phone)
    {
        var digits = phone.Trim();
        if (digits.Length >= 11) return digits[..3] + "****" + digits[^4..];
        if (digits.Length >= 7) return digits[..2] + "****" + digits[^3..];
        return "****";
    }

    /// <summary>服务端按来源 IP 判定地区不允许时返回 403 + region_blocked。</summary>
    private static bool IsRegionBlocked(RelayApiException ex) =>
        ex.Status == System.Net.HttpStatusCode.Forbidden &&
        (ex.Body?.Contains("region_blocked", StringComparison.Ordinal) ?? false);

    /// <summary>只展示房间码与游戏信息，连接地址由联机组件内部使用。</summary>
    public sealed class RoomItem
    {
        public RoomItem(RelayAllocation allocation)
        {
            Allocation = allocation;
        }

        public RelayAllocation Allocation { get; }
        public bool CanCopy => RelayRoomCode.TryNormalize(Allocation.RoomCode, out _);
        public string RoomCode => CanCopy ? Allocation.RoomCode! : "旧版房间（请关闭后重新创建）";
        public string NodeName => Allocation.NodeName;
        public string Label => string.IsNullOrWhiteSpace(Allocation.GameVersion)
            ? Allocation.Label : Allocation.Label + " · " + Allocation.GameVersion;
        public string StateText => Allocation.RoomReady ? "运行中" : "等待游戏连接";
        public string UsageText => FormatSize(Allocation.BytesIn + Allocation.BytesOut);
    }
}
