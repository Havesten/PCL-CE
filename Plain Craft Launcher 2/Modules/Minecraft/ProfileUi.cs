using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Core.IO.Net;
using PCL.Core.Logging;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.IdentityModel.OAuth;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.UI.Dialogs;
using PCL.Core.Utils;
using PCL.Core.Utils.Validate;
using PCL.Network;

namespace PCL;

/// <summary>
/// WPF-only profile workflows. Profile state and persistence live in the Core ProfileService.
/// </summary>
public static class ProfileUi
{
    private static int _isMsSkinChanging;

    /// <summary>
    ///     处理微软登录中的 XSTS 账号类错误，向用户展示对应的操作指引。
    /// </summary>
    /// <returns>该异常是否属于已知的 XSTS 账号错误并已被处理。</returns>
    public static bool HandleMicrosoftXstsError(IdentityModelAuthenticationException exception)
    {
        if (exception.Error is not { } error || !error.StartsWith("xsts_", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedError = error.ToLowerInvariant();
        if (normalizedError is not ("xsts_2148916227" or "xsts_2148916233" or "xsts_2148916235" or "xsts_2148916238"))
            return false;

        Common.RunInUiWait(() =>
        {
            switch (normalizedError)
            {
                case "xsts_2148916227":
                    _ = DialogService.Instance.ShowAsync(new TextDialogRequest
                    {
                        Title = Lang.Text("Minecraft.Launch.Login.Failed"),
                        Content = Lang.Text("Minecraft.Launch.Login.Microsoft.Banned"),
                        Buttons = DialogButtons.From(
                            DialogButton.Create(Lang.Text("Minecraft.Launch.Login.IKnow"))),
                        IsWarn = true,
                        ForceWait = true
                    });
                    break;
                case "xsts_2148916233":
                    if (DialogService.Instance.Show(new TextDialogRequest
                        {
                            Title = Lang.Text("Minecraft.Launch.Login.Hint"),
                            Content = Lang.Text("Minecraft.Launch.Login.Microsoft.XboxNotRegistered"),
                            Buttons = DialogButtons.From(
                                DialogButton.Create(Lang.Text("Minecraft.Launch.Login.Register")),
                                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                            ForceWait = true
                        }).Result?.Button == 1)
                        Common.OpenWebsite("https://signup.live.com/signup");
                    break;
                case "xsts_2148916235":
                    _ = DialogService.Instance.ShowAsync(new TextDialogRequest
                    {
                        Title = Lang.Text("Minecraft.Launch.Login.Failed"),
                        Content = Lang.Text("Minecraft.Launch.Login.Microsoft.RegionBlocked"),
                        Buttons = DialogButtons.From(
                            DialogButton.Create(Lang.Text("Minecraft.Launch.Login.IKnow"))),
                        ForceWait = true
                    });
                    break;
                case "xsts_2148916238":
                    if (DialogService.Instance.Show(new TextDialogRequest
                        {
                            Title = Lang.Text("Minecraft.Launch.Login.Hint"),
                            Content = Lang.Text("Minecraft.Launch.Login.Microsoft.Underage.Message"),
                            Buttons = DialogButtons.From(
                                DialogButton.Create(Lang.Text("Minecraft.Launch.Login.Microsoft.Underage.AgeOver13")),
                                DialogButton.Create(Lang.Text("Minecraft.Launch.Login.Microsoft.Underage.AgeUnder13")),
                                DialogButton.Create(Lang.Text("Common.Option.IDontKnow"))),
                            ForceWait = true
                        }).Result?.Button == 1)
                    {
                        Common.OpenWebsite("https://account.live.com/editprof.aspx");
                        DialogService.Instance.Show(new TextDialogRequest
                        {
                            Title = Lang.Text("Minecraft.Launch.Login.Hint"),
                            Content = Lang.Text("Minecraft.Launch.Login.Microsoft.ChangeBirthDate.Message"),
                            Buttons = DialogButtons.Ok,
                            ForceWait = true
                        });
                    }
                    else
                    {
                        Common.OpenWebsite(
                            "https://support.microsoft.com/zh-cn/account-billing/如何更改-microsoft-帐户上的出生日期-837badbc-999e-54d2-2617-d19206b9540a");
                        DialogService.Instance.Show(new TextDialogRequest
                        {
                            Title = Lang.Text("Minecraft.Launch.Login.Hint"),
                            Content = Lang.Text("Minecraft.Launch.Login.Microsoft.ChangeBirthDate.SupportMessage"),
                            Buttons = DialogButtons.Ok,
                            ForceWait = true
                        });
                    }

                    break;
            }
        });
        return true;
    }

    /// <summary>
    ///     处理微软账号未购买 Minecraft 的错误，提供购买入口。
    /// </summary>
    /// <returns>该异常是否属于未购买错误并已被处理。</returns>
    public static bool HandleMicrosoftNotOwnedError(IdentityModelAuthenticationException exception)
    {
        if (!string.Equals(exception.Error, "minecraft_not_owned", StringComparison.OrdinalIgnoreCase))
            return false;

        Common.RunInUiWait(() =>
        {
            if (DialogService.Instance.Show(new TextDialogRequest
                {
                    Title = Lang.Text("Minecraft.Launch.Login.Failed"),
                    Content = Lang.Text("Minecraft.Launch.Login.Microsoft.NotPurchased"),
                    Buttons = DialogButtons.From(
                        DialogButton.Create(Lang.Text("Minecraft.Launch.Login.Microsoft.PurchaseMinecraft")),
                        DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                    ForceWait = true
                }).Result?.Button == 1)
                Common.OpenWebsite(
                    "https://www.xbox.com/zh-cn/games/store/minecraft-java-bedrock-edition-for-pc/9nxp44l49shj");
        });
        return true;
    }

    /// <summary>
    ///     处理微软账号尚未创建 Minecraft 玩家档案的错误，提供建档入口。
    /// </summary>
    /// <returns>该异常是否属于未创建档案错误并已被处理。</returns>
    public static bool HandleMicrosoftCreateProfileError(IdentityModelAuthenticationException exception)
    {
        if (!string.Equals(exception.Error, "profile_not_created", StringComparison.OrdinalIgnoreCase))
            return false;

        Common.RunInUiWait(() =>
        {
            if (DialogService.Instance.Show(new TextDialogRequest
                {
                    Title = Lang.Text("Minecraft.Launch.Login.Failed"),
                    Content = Lang.Text("Minecraft.Launch.Login.Microsoft.CreateProfile.Message"),
                    Buttons = DialogButtons.From(
                        DialogButton.Create(Lang.Text("Minecraft.Launch.Login.Microsoft.CreateProfile.Button")),
                        DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                    ForceWait = true
                }).Result?.Button == 1)
                Common.OpenWebsite("https://www.minecraft.net/zh-hans/msaprofile/mygames/editprofile");
        });
        return true;
    }

    public static Task<AuthorizeResult?> ShowDeviceCodeLoginAsync(DeviceCodeAuthenticationContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<AuthorizeResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new LoginDialogRequest
        {
            Title = Lang.Text("Launch.Account.LoginDialog.MinecraftLogin"),
            // 登录弹窗使用自绘按钮，与旧实现一样不提供按钮组
            Buttons = default,
            ForceWait = true,
            Data = JsonSerializer.SerializeToNode(context.Data, JsonCompat.SerializerOptions)!.AsObject(),
            DeviceCodePoll = (_, pollToken) => context.PollAsync(pollToken),
            LoginResultHandler = (oauth, _) =>
            {
                completion.TrySetResult(oauth);
                return Task.CompletedTask;
            },
            CompletionHandler = result =>
            {
                if (result is Exception ex) completion.TrySetException(ex);
            }
        };
        Launcher.ShowDeviceCodeLogin(request);
        return completion.Task;
    }

    public static object McLoginMojangUuid(string name, bool throwOnNotFound)
    {
        if (string.IsNullOrWhiteSpace(name)) return Common.StrFill("", "0", 32);
        var uuid = Common.ReadIni(Common.pathTemp + @"Cache\Uuid\Mojang.ini", name);
        if (uuid?.Length == 32) return uuid;
        try
        {
            JsonObject? json = null;
            var finished = false;
            Common.RunInNewThread(() =>
            {
                try { json = (JsonObject)NetFacade.NetGetCodeByRequestRetry("https://api.mojang.com/users/profiles/minecraft/" + name, isJson: true); }
                catch { }
                finally { finished = true; }
            }, $"{name} Uuid Get");
            while (!finished) Thread.Sleep(50);
            if (json is null) throw new FileNotFoundException("正版玩家档案不存在（" + name + "）");
            uuid = json["id"]?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Common.Log(ex, "从官网获取正版 UUID 失败（" + name + "）");
            throw;
        }
        if (uuid.Length != 32) throw new FormatException("获取的正版 UUID 长度不足（" + uuid + "）");
        Common.WriteIni(Common.pathTemp + @"Cache\Uuid\Mojang.ini", name, uuid);
        return uuid;
    }

    /// <summary>
    ///     新建档案仅支持 Microsoft 账号登录。离线登录与第三方验证的建档入口已随相关代码一并移除。
    /// </summary>
    public static async Task CreateProfileAsync()
    {
        ProfileService.IsCreatingProfile = true;
        Common.RunInUi(() => Launcher.frmLaunchLeft.RefreshPage(true, toMicrosoftLogin: true));
        await Task.CompletedTask;
    }

    public static void EditProfileId() => _ = EditProfileIdAsync();

    private static async Task EditProfileIdAsync()
    {
        var profile = ProfileService.Current;
        if (profile is null) return;
        if (profile.ProfileType == ProfileType.Microsoft)
        {
            var usernameDialog = await DialogService.Instance.ShowAsync(new InputDialogRequest
            {
                Title = Lang.Text("Launch.Account.Profile.EditPlayerId.Title"),
                Content = Lang.Text("Launch.Account.Profile.EditPlayerId.MicrosoftWarning"),
                DefaultValue = profile.UserName,
                ValidateRules = [new StringLengthValidator(3, 16), new RegexValidator("([A-z]|[0-9]|_)+")],
                HintText = Lang.Text("Launch.Account.Profile.EditPlayerId.Hint"),
                Buttons = DialogButtons.OkCancel,
                HighLight = false,
                ForceWait = false
            });
            var username = usernameDialog.IsConfirmed ? usernameDialog.InputText : null;
            if (string.IsNullOrWhiteSpace(username)) return;
            if ((await DialogService.Instance.ShowAsync(new TextDialogRequest
                {
                    Title = Lang.Text("Launch.Account.Profile.EditPlayerId.Confirm.Title"),
                    Content = Lang.Text("Launch.Account.Profile.EditPlayerId.Confirm.Message"),
                    Buttons = DialogButtons.From(
                        DialogButton.Create(Lang.Text("Common.Action.Continue")),
                        DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                    IsWarn = true,
                    ForceWait = false
                })).Button == 2) return;
            Common.RunInNewThread(() =>
            {
                try
                {
                    var check = (JsonObject)Common.GetJson(Requester.Fetch(
                        $"https://api.minecraftservices.com/minecraft/profile/name/{username}/available",
                        new FetchParam { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + profile.AccessToken } }));
                    var status = check["status"]?.ToString();
                    if (status is "DUPLICATE" or "NOT_ALLOWED")
                    {
                        DialogService.Instance.Show(new TextDialogRequest
                        {
                            Title = Lang.Text("Launch.Account.Profile.EditPlayerId.Failed.Title"),
                            Content = status == "DUPLICATE"
                                ? Lang.Text("Launch.Account.Profile.EditPlayerId.Duplicate")
                                : Lang.Text("Launch.Account.Profile.EditPlayerId.NotAllowed"),
                            Buttons = DialogButtons.Ok,
                            IsWarn = true,
                            ForceWait = true
                        });
                        return;
                    }
                    var result = (JsonObject)Common.GetJson(Requester.Fetch(
                        $"https://api.minecraftservices.com/minecraft/profile/name/{username}",
                        new FetchParam { Method = "PUT", ContentType = "application/json", Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + profile.AccessToken } }));
                    var updated = profile.Clone();
                    updated.UserName = result["name"]?.ToString() ?? username;
                    ProfileService.Update(profile, updated);
                    ProfileService.Select(updated);
                    HintService.Show(Hints.Success(Lang.Text("Launch.Account.Profile.EditPlayerId.Success", updated.UserName)));
                    Common.RunInUi(() => { Launcher.frmLoginProfileSkin?.Reload(); Launcher.frmLaunchLeft.RefreshPage(true); });
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    DialogService.Instance.Show(new TextDialogRequest
                    {
                        Title = Lang.Text("Launch.Account.Profile.EditPlayerId.Failed.Title"),
                        Content = Lang.Text("Launch.Account.Profile.EditPlayerId.Cooldown"),
                        Buttons = DialogButtons.Ok,
                        ForceWait = true
                    });
                }
                catch (Exception ex)
                {
                    Common.Log(ex, Lang.Text("Launch.Account.Profile.Error.ChangeId"), Common.LogLevel.Msgbox,
                        userSummary: Lang.Text("Launch.Account.Profile.Error.ChangeId"));
                }
            });
            return;
        }
    }

    public static void RemoveProfile(McProfile profile)
    {
        ProfileService.Remove(profile);
        HintService.Show(Hints.Success(Lang.Text("Launch.Account.Profile.Deleted")));
    }

    public static string GetProfileInfo(McProfile profile)
    {
        var info = Lang.Text("Launch.Account.Type.Microsoft");
        return string.IsNullOrWhiteSpace(profile.Description) ? info : $"{info}，{profile.Description}";
    }

    public static GameLaunch.McLoginData GetLoginData()
    {
        return new GameLaunch.McLoginMs();
    }

    public static string IsProfileValid()
    {
        var profile = ProfileService.Current;
        if (profile is null) return Lang.Text("Minecraft.Launch.Precheck.NoProfile");
        return string.Empty;
    }

    public static void ChangeSkinMs()
    {
        if (Interlocked.Exchange(ref _isMsSkinChanging, 1) != 0)
        {
            HintService.Show(Hints.Info(Lang.Text("Launch.Skin.Change.Busy")));
            return;
        }

        var profile = ProfileService.Current;
        if (profile is null)
        {
            Interlocked.Exchange(ref _isMsSkinChanging, 0);
            return;
        }
        var profileId = profile.ProfileId;
        if (GameLaunch.mcLoginLoader.State == Common.LoadState.Failed)
        {
            Interlocked.Exchange(ref _isMsSkinChanging, 0);
            HintService.Show(Hints.Error(Lang.Text("Launch.Skin.Change.LoginFailed")));
            return;
        }
        var skinInfo = Skin.McSkinSelect();
        if (!skinInfo.IsValid)
        {
            Interlocked.Exchange(ref _isMsSkinChanging, 0);
            return;
        }
        HintService.Show(Hints.Info(Lang.Text("Launch.Skin.Change.Starting")));
        Common.RunInNewThread(() =>
        {
            try
            {
                var hasRetriedAuthentication = false;
                while (true)
                {
                    if (GameLaunch.mcLoginMsLoader.State == Common.LoadState.Loading) GameLaunch.mcLoginMsLoader.WaitForExit();
                    if (GameLaunch.mcLoginMsLoader.State != Common.LoadState.Finished) GameLaunch.mcLoginMsLoader.WaitForExit(GetLoginData());
                    if (GameLaunch.mcLoginMsLoader.State != Common.LoadState.Finished)
                        throw new InvalidOperationException("Microsoft login failed");
                    var latestProfile = ProfileService.Profiles.FirstOrDefault(item => item.ProfileId == profileId)
                                        ?? throw new InvalidOperationException("Microsoft profile no longer exists.");
                    using var contents = new MultipartFormDataContent
                    {
                        { new StringContent(skinInfo.IsSlim ? "slim" : "classic"), "variant" },
                        { new ByteArrayContent(Common.ReadFileBytes(skinInfo.LocalFile)), "file", Common.GetFileNameFromPath(skinInfo.LocalFile) }
                    };
                    var result = Requester.Fetch("https://api.minecraftservices.com/minecraft/profile/skins", new FetchParam
                    {
                        Method = "POST", Content = contents,
                        Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + latestProfile.AccessToken }
                    });
                    if (result.Contains("request requires user authentication"))
                    {
                        if (hasRetriedAuthentication)
                            throw new IdentityModelAuthenticationException("invalid_token", "Microsoft authentication remained invalid.");
                        hasRetriedAuthentication = true;
                        HintService.Show(Hints.Info(Lang.Text("Launch.Skin.Change.Reauthenticating")));
                        GameLaunch.mcLoginMsLoader.Start(GetLoginData(), true);
                        continue;
                    }
                    var json = (JsonObject)Common.GetJson(result);
                    var active = json["skins"]?.AsArray().FirstOrDefault(s => s?["state"]?.ToString() == "ACTIVE")?.AsObject();
                    if (active?["url"] is not null) MySkin.ReloadCache(active["url"]!.ToString(), latestProfile.Uuid);
                    else throw new InvalidDataException(json["errorMessage"]?.ToString() ?? result);
                    break;
                }
            }
            catch (Exception ex)
            {
                Common.Log(ex, Lang.Text("Launch.Account.Profile.Error.ChangeSkin"), Common.LogLevel.Hint,
                    userSummary: Lang.Text("Launch.Account.Profile.Error.ChangeSkin"));
            }
            finally
            {
                Interlocked.Exchange(ref _isMsSkinChanging, 0);
            }
        }, "Ms Skin Upload");
    }
}
