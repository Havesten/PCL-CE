using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Dialogs;

namespace PCL;

/// <summary>
///     绑定手机号弹窗。号码规则、发码与绑定全部由服务端裁决，这里只做交互与本地预校验。
/// </summary>
public partial class MyMsgPhoneBind
{
    private readonly DialogViewModel myDialog;
    private readonly int uuid = Common.GetUuid();
    private bool _busy;
    private string? _captchaId;

    public MyMsgPhoneBind(DialogViewModel dialog)
    {
        try
        {
            InitializeComponent();
            AppendUniqueNameSuffix(BtnBind);
            AppendUniqueNameSuffix(BtnLater);
            AppendUniqueNameSuffix(BtnRefreshCaptcha);
            AppendUniqueNameSuffix(BtnSendCode);
            myDialog = dialog;
            LabTitle.Text = dialog.Title;
            LabText.Text = dialog.TextContent;
            LabText.Visibility = string.IsNullOrEmpty(dialog.TextContent) ? Visibility.Collapsed : Visibility.Visible;
            ShapeLine.StrokeThickness = Common.GetWpfSize(1d);

            var primary = dialog.ButtonText(1);
            if (!string.IsNullOrEmpty(primary)) BtnBind.Text = primary;
            var secondary = dialog.ButtonText(2);
            BtnLater.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
            if (!string.IsNullOrEmpty(secondary)) BtnLater.Text = secondary;
        }

        catch (Exception ex)
        {
            Common.Log(
                ex,
                "绑定手机号弹窗初始化失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }

        Loaded += Load;
    }

    private void AppendUniqueNameSuffix(FrameworkElement element)
    {
        element.Name += Common.GetUuid();
    }

    private async void Load(object sender, EventArgs e)
    {
        try
        {
            // 动画（与其它弹窗保持一致）
            Opacity = 0d;
            Animations.AniStart(
                Animations.AaColor(Launcher.frmMain.PanMsgBackground, BlurBorder.BackgroundProperty,
                    new Common.MyColor(90d, 0d, 0d, 0d) - Launcher.frmMain.PanMsgBackground.Background, 200),
                "PanMsgBackground Background");
            Animations.AniStart(
                new[]
                {
                    Animations.AaOpacity(this, 1d, 120, 60),
                    Animations.AaDouble(i => TransformPos.Y += (double)i,
                        -TransformPos.Y, 300, 60,
                        new Animations.AniEaseOutBack(Animations.AniEasePower.Weak)),
                    Animations.AaDouble(i => TransformRotate.Angle += (double)i,
                        -TransformRotate.Angle, 300, 60,
                        new Animations.AniEaseOutFluent(Animations.AniEasePower.Weak))
                }, "MyMsgBox " + uuid);
            Common.Log("[Control] 绑定手机号弹窗：" + LabTitle.Text);
            TextMobile.Focus();
            await RefreshCaptchaAsync().ConfigureAwait(true);
        }

        catch (Exception ex)
        {
            Common.Log(
                ex,
                "绑定手机号弹窗加载失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
    }

    private void Close()
    {
        Animations.AniStart(new[]
        {
            Animations.AaCode(() =>
            {
                if (!DialogService.Instance.IsShowing && !DialogService.Instance.HasPending)
                    Animations.AniStart(Animations.AaColor(Launcher.frmMain.PanMsgBackground,
                        BlurBorder.BackgroundProperty,
                        new Common.MyColor(0d, 0d, 0d, 0d) - Launcher.frmMain.PanMsgBackground.Background, 200,
                        ease: new Animations.AniEaseOutFluent(Animations.AniEasePower.Weak)));
            }, 30),
            Animations.AaOpacity(this, -Opacity, 80, 20),
            Animations.AaDouble(i => TransformPos.Y += (double)i, 20d - TransformPos.Y,
                150, 0, new Animations.AniEaseOutFluent()),
            Animations.AaDouble(i => TransformRotate.Angle += (double)i,
                6d - TransformRotate.Angle, 150, 0,
                new Animations.AniEaseInFluent(Animations.AniEasePower.Weak)),
            Animations.AaCode(() => ((Grid)Parent).Children.Remove(this), after: true)
        }, "MyMsgBox " + uuid);
    }

    // ---------------------------------------------------------------- 交互

    private async Task RefreshCaptchaAsync()
    {
        var request = myDialog.PhoneBind;
        if (request is null) return;
        try
        {
            var (id, prompt) = await request.GetCaptchaAsync().ConfigureAwait(true);
            _captchaId = id;
            LabCaptcha.Text = prompt;
            TextCaptcha.Text = string.Empty;
        }
        catch (Exception ex)
        {
            SetStatus("获取人机验证失败：" + ex.Message);
        }
    }

    private async void BtnRefreshCaptcha_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        await RefreshCaptchaAsync().ConfigureAwait(true);
    }

    private async void BtnSendCode_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        var request = myDialog.PhoneBind;
        if (request is null) return;

        var phone = TextMobile.Text.Trim();
        if (!ValidPhone(phone))
        {
            SetStatus("手机号格式不正确：需 11 位、1 开头，且不能有 7 位及以上连号");
            return;
        }

        if (_captchaId is null)
        {
            await RefreshCaptchaAsync().ConfigureAwait(true);
            return;
        }

        if (TextCaptcha.Text.Trim().Length == 0)
        {
            SetStatus("请填写人机验证的答案");
            return;
        }

        SetBusy(true, "正在发送…");
        try
        {
            var devCode = await request
                .SendSmsAsync(phone, _captchaId, TextCaptcha.Text.Trim())
                .ConfigureAwait(true);
            LabSmsHint.Text = devCode is null
                ? "已发送，请查收短信（10 分钟内有效）"
                : $"开发模式验证码：{devCode}";
            if (devCode is not null) TextCode.Text = devCode;
            _captchaId = null; // 人机验证一题一用
            SetStatus("验证码已发送");
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

    private async void BtnBind_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy || myDialog.IsClosed) return;
        var request = myDialog.PhoneBind;
        if (request is null) return;

        var phone = TextMobile.Text.Trim();
        var code = TextCode.Text.Trim();
        if (!ValidPhone(phone))
        {
            SetStatus("手机号格式不正确：需 11 位、1 开头，且不能有 7 位及以上连号");
            return;
        }

        if (code.Length == 0)
        {
            SetStatus("请填写短信验证码");
            return;
        }

        SetBusy(true, "正在绑定…");
        try
        {
            await request.BindAsync(phone, code).ConfigureAwait(true);
            myDialog.InputText = phone;
            myDialog.CloseByButton(1);
            Close();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            SetBusy(false);
        }
    }

    private void BtnLater_Click(object sender, MouseButtonEventArgs e)
    {
        if (myDialog.IsClosed) return;
        myDialog.CloseByButton(2);
        Close();
    }

    // ---------------------------------------------------------------- 工具

    private void SetStatus(string text) => LabStatus.Text = text;

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        BtnBind.IsEnabled = !busy;
        BtnLater.IsEnabled = !busy;
        BtnSendCode.IsEnabled = !busy;
        BtnRefreshCaptcha.IsEnabled = !busy;
        if (status is not null) LabStatus.Text = status;
    }

    /// <summary>
    ///     与云端同口径：11 位、1 开头，禁止 7 位及以上连续相同数字（如 13333333333）。
    /// </summary>
    private static bool ValidPhone(string p)
    {
        if (p.Length != 11 || p[0] != '1') return false;
        foreach (var c in p)
            if (c < '0' || c > '9')
                return false;
        var run = 1;
        for (var i = 1; i < p.Length; i++)
        {
            if (p[i] != p[i - 1])
            {
                run = 1;
                continue;
            }

            run++;
            if (run >= 7) return false;
        }

        return true;
    }
}
