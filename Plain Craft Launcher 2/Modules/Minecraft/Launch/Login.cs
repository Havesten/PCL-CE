using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.UI.Dialogs;

namespace PCL;

public static partial class GameLaunch
{
    public static Loading.LoaderTask<McLoginData, McLoginResult> mcLoginLoader =
        new(
            Lang.Text("Minecraft.Launch.Stage.Login"),
            McLoginStart,
            inputDelegate: McLoginInput,
            priority: ThreadPriority.BelowNormal)
        {
            reloadTimeout = 1,
            ProgressWeight = 15d,
            block = false,
            Identifier = LaunchStageLoginId,
            DisplayNameFactory = () => Lang.Text("Minecraft.Launch.Stage.Login")
        };

    public static Loading.LoaderTask<McLoginMs, McLoginResult> mcLoginMsLoader =
        new("Loader Login Ms", McLoginMsStart)
        {
            reloadTimeout = 1
        };

    public static McLoginData? McLoginInput()
    {
        McLoginData? loginData = null;
        try
        {
            loginData = ProfileUi.GetLoginData();
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                Lang.Text("Minecraft.Launch.Login.Error.Input"),
                Common.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Launch.Login.Error.Input"));
        }

        return loginData;
    }

    private static void McLoginStart(Loading.LoaderTask<McLoginData, McLoginResult> data)
    {
        Common.Log("[Profile] 开始加载选定档案");
        var checkResult = ProfileUi.IsProfileValid();
        if (!string.IsNullOrEmpty(checkResult))
            throw new ArgumentException(checkResult);

        mcLoginMsLoader.WaitForExit((McLoginMs)data.input!, mcLoginLoader, data.isForceRestarting);
        data.output = mcLoginMsLoader.output;

        Common.RunInUi(() => Launcher.frmLaunchLeft!.RefreshPage(false));
        Common.Log("[Profile] 选定档案加载完成");
    }

    private static void McLoginMsStart(Loading.LoaderTask<McLoginMs, McLoginResult> data)
    {
        var existing =
            ProfileService.Current?.ProfileType == ProfileType.Microsoft
                ? ProfileService.Current
                : null;
        LogWrapper.Info(
            "Profile",
            $"验证方式：正版（{(existing is null ? "尚未登录" : existing.UserName)}）");
        data.Progress = 0.05d;
        var stored = _AuthenticateMicrosoft(data, existing);
        ThrowIfAborted(data);
        ProfileService.IsCreatingProfile = false;

        data.output = _CreateMicrosoftLoginResult(stored);

        data.Progress = 0.98d;
        LogWrapper.Info("Profile", "正版验证完成");
    }

    private static McProfile _AuthenticateMicrosoft(
        Loading.LoaderTask<McLoginMs, McLoginResult> data,
        McProfile? existing)
    {
        try
        {
            return ProfileService
                .AuthenticateAsync(
                    ProfileType.Microsoft,
                    new AuthenticationRequest
                    {
                        ForceRefresh = data.isForceRestarting,
                        DeviceCodeHandler = ProfileUi.ShowDeviceCodeLoginAsync,
                        RefreshFailureHandler = _ConfirmMicrosoftRefreshFailureAsync
                    },
                    existing,
                    true,
                    data.AbortedToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (IdentityModelAuthenticationException ex)
            when (ProfileUi.HandleMicrosoftXstsError(ex)
                  || ProfileUi.HandleMicrosoftNotOwnedError(ex)
                  || ProfileUi.HandleMicrosoftCreateProfileError(ex))
        {
            throw new IdentityModelException("$$", ex);
        }
    }

    private static McLoginResult _CreateMicrosoftLoginResult(McProfile stored)
    {
        return new McLoginResult
        {
            AccessToken = stored.AccessToken,
            Name = stored.UserName,
            Uuid = stored.Uuid,
            Type = "Microsoft",
            ClientToken = stored.ClientToken,
            ProfileJson = stored.RawJson
        };
    }

    /// <summary>
    ///     检查是否被中断
    /// </summary>
    private static void ThrowIfAborted(Loading.LoaderTask<McLoginMs, McLoginResult> data)
    {
        if (data.IsAborted)
            throw new ThreadInterruptedException();
    }

    private static Task<bool> _ConfirmMicrosoftRefreshFailureAsync(
        Exception exception,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LogWrapper.Info("Profile", $"获取正版 OAuth Token 失败：{exception}");
        var reuseCachedProfile = false;
        Common.RunInUiWait(() =>
        {
            if (!isLaunching)
                return;
            reuseCachedProfile =
                DialogService.Instance.Show(new TextDialogRequest
                    {
                        Title = Lang.Text("Minecraft.Launch.Login.RefreshAccountFailed.Title"),
                        Content = Lang.Text("Minecraft.Launch.Login.RefreshAccountFailed.Message"),
                        Buttons = DialogButtons.From(
                            DialogButton.Create(Lang.Text("Minecraft.Launch.Login.Continue")),
                            DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                        ForceWait = true
                    }).Result
                    ?.Button == 1;
        });
        return Task.FromResult(reuseCachedProfile);
    }

    public abstract class McLoginData
    {
        public override bool Equals(object? obj)
        {
            return obj is not null && obj.GetHashCode() == GetHashCode();
        }
    }

    public class McLoginMs : McLoginData
    {
        public override int GetHashCode()
        {
            return (int)Math.Round(Common.GetHash(nameof(McLoginMs)) % (decimal)int.MaxValue);
        }
    }

    public struct McLoginResult
    {
        public string Name;
        public string Uuid;
        public string AccessToken;
        public string Type;
        public string ClientToken;

        /// <summary>
        ///     进行微软登录时返回的 profile 信息。
        /// </summary>
        public string ProfileJson;
    }
}
