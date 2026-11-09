using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Core.UI.Dialogs;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

public static class UpdateManager
{
    public static bool isUpdateWaitingRestart;

    /// <summary>
    ///     更新源。目前只指向自建服务（cloud/pcl-cloud），不再向社区公共源发起请求。
    ///     <para>
    ///         地址统一由 <see cref="CloudEndpoints" /> 提供，与中继联机 API 共用同一个根地址。
    ///         自建服务的 Web 后台「更新」独立配置页保存后生成 <c>apiv2/updateinfo.json</c>，
    ///         启动器据此获取：是否强制更新、最新版本、强制最低版本、下载直链、更新日志与公告。
    ///     </para>
    /// </summary>
    private static readonly UpdatesCloudModel _cloudSource = new(CloudEndpoints.UpdateRoot, "PCLCE-NS Cloud");

    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource> { _cloudSource });

    /// <summary>
    ///     是否启用启动器在线服务：启动时的自动更新检查、服务端公告、更新页的自动检查。
    ///     <para>
    ///         更新源为自建云端（<c>apiv2/updateinfo.json</c>），配置由 Web 后台「更新」页管理。
    ///     </para>
    /// </summary>
    public const bool OnlineServiceEnabled = true;

    /// <summary>
    ///     从云端获取更新日志正文（updateinfo.json 的 changelog 字段），
    ///     供「查看更新日志」按钮展示。每次都重新拉取，保证内容最新。
    /// </summary>
    public static string FetchCloudChangelog()
    {
        _cloudSource.RefreshCache();
        return _cloudSource.Changelog;
    }

    /// <summary>
    ///     是否命中强制更新：云端「是否强制更新」开启，
    ///     或当前版本低于云端「强制最低版本」（留空则不按版本强制）。
    /// </summary>
    public static bool IsForceRequired(VersionDataModel version)
    {
        if (version is null)
            return false;
        if (version.ForceUpdate)
            return true;
        try
        {
            if (!string.IsNullOrWhiteSpace(version.MinVersion))
                return SemVer.Parse(Common.VersionBaseName) < SemVer.Parse(version.MinVersion);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Update] 强制最低版本号解析失败");
        }

        return false;
    }

    public static bool IsCurrentVersionBeta
    {
        get
        {
            if (Common.VersionBaseName.Contains("beta"))
                return true;
            return (int)Config.Update.UpdateChannel == 1;
        }
    }
    
    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        // 在线服务关闭时没有版本元数据可用，直接给出「未知」，避免误报「启动器已过期」
        if (!OnlineServiceEnabled) return UpdateEnums.VersionStatus.Unknown;

        try
        {
            if (IsCurrentVersionBeta && (int)Config.Update.UpdateChannel != 1)
            {
                var isNewerThanStable = remoteServer.IsLatest(UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64,
                    SemVer.Parse(Common.VersionBaseName),
                    Common.VersionCode);
                var isBetaLatest = remoteServer.IsLatest(UpdateChannel.beta,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64,
                    SemVer.Parse(Common.VersionBaseName),
                    Common.VersionCode);
                return isNewerThanStable && isBetaLatest
                    ? UpdateEnums.VersionStatus.Latest
                    : UpdateEnums.VersionStatus.NotLatest;
            }

            return remoteServer.IsLatest(
                IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64,
                SemVer.Parse(Common.VersionBaseName),
                Common.VersionCode)
                ? UpdateEnums.VersionStatus.Latest
                : UpdateEnums.VersionStatus.NotLatest;
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                Lang.Text("Update.Check.Failed"),
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Update.Check.Failed"));
            return UpdateEnums.VersionStatus.Unknown;
        }
    }
    
    public static Loading.LoaderCombo<JsonObject> updateLoader;

    public static void UpdateStart(UpdateEnums.UpdateType type, string receivedKey = null, bool forceValidated = false)
    {
        var dlTargetPath = Common.ExePath + @"PCL\Plain Craft Launcher Community Edition.exe";
        Common.RunInNewThread(() =>
        {
            var effectiveType = type;
            try
            {
                var version = remoteServer.GetLatestVersion(
                    IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64
                );

                Common.WriteFile($"{Common.pathTemp}CEUpdateLog.md", version.Changelog);
                Common.Log(
                    $"[Update] 远程最新版本: {version.VersionName}, 当前版本: {Common.VersionBaseName}");
                if (!(SemVer.Parse(version.VersionName) > SemVer.Parse(Common.VersionBaseName)))
                    return;

                // 强制更新（云端「是否强制更新」或低于「强制最低版本」）不允许跳过：
                // 即使更新设置为「仅提示」，也直接下载并安装。
                effectiveType = type;
                if (type == UpdateEnums.UpdateType.PromptOnly && IsForceRequired(version))
                {
                    Common.Log("[Update] 命中强制更新，跳过询问直接下载安装");
                    effectiveType = UpdateEnums.UpdateType.UpdateNow;
                }

                if (effectiveType == UpdateEnums.UpdateType.PromptOnly)
                {
                    Common.RunInUi(() =>
                    {
                        if (DialogService.Instance.Show(new TextDialogRequest
                            {
                                Title = Lang.Text("Update.Title"),
                                Content = Lang.Text("Update.Available", Common.VersionBaseName,
                                    version.VersionName),
                                Buttons = DialogButtons.From(
                                    DialogButton.Create(Lang.Text("Update.Action")),
                                    DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                                ForceWait = true
                            }).Result?.Button == 1)
                            Launcher.frmMain.PageChange(FormMain.PageType.Setup,
                                FormMain.PageSubType.SetupUpdate);
                    });
                    return;
                    // 构造步骤加载器
                }

                var loaders = new List<Loading.LoaderBase>();
                // 下载
                loaders.AddRange(remoteServer.GetDownloadLoader(
                    IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, dlTargetPath));
                loaders.Add(new Loading.LoaderTask<int, int>(Lang.Text("Update.Task.Check"), _ =>
                {
                    // 云端下载直链不附带哈希时跳过校验
                    if (string.IsNullOrWhiteSpace(version.Sha256))
                        return;
                    var curHash = Common.GetFileSha256(dlTargetPath);
                    if ((curHash ?? "") != (version.Sha256 ?? ""))
                        throw new Exception(Lang.Text("Update.Error.Sha256Mismatch", version.Sha256, curHash));
                }));
                if (effectiveType == UpdateEnums.UpdateType.UpdateNow)
                    loaders.Add(new Loading.LoaderTask<int, int>(Lang.Text("Update.Task.Install"),
                        _ => UpdateRestart(true)));
                else if (effectiveType == UpdateEnums.UpdateType.Silent)
                    loaders.Add(new Loading.LoaderTask<int, int>(Lang.Text("Update.Task.Prepare"),
                        _ => isUpdateWaitingRestart = true));
                else if (effectiveType == UpdateEnums.UpdateType.DownloadAndPrompt)
                    loaders.Add(new Loading.LoaderTask<int, int>(
                        Lang.Text("Update.Task.ShowButton"), _ =>
                    {
                        isUpdateWaitingRestart = true;
                        Common.RunInUi(() =>
                        {
                            LocalizedText.TryBind(
                                Launcher.frmMain.BtnExtraUpdateRestart,
                                "ToolTip",
                                () => Lang.Text(
                                    "Main.Extra.UpdateRestart.ToolTipWithVersion",
                                    Common.VersionBaseName,
                                    version.VersionName));
                            Launcher.frmMain.BtnExtraUpdateRestart.ShowRefresh();
                            Launcher.frmMain.BtnExtraUpdateRestart.Ribble();
                        });
                    })
                    {
                        show = false
                    });
                loaders.Add(new Loading.LoaderTask<int, int>(
                    Lang.Text("Update.Task.RefreshSettings"), _ =>
                {
                    if (Launcher.frmSetupUpdate is not null)
                        Common.RunInUi(() =>
                        {
                            Launcher.frmSetupUpdate.BtnUpdate.TrySetLocalizedValue(
                                "Text",
                                "Update.Task.RestartInstall");
                            Launcher.frmSetupUpdate.BtnUpdate.IsEnabled = true;
                        });
                })
                {
                    show = false
                });
                // 启动
                updateLoader =
                    new Loading.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders);
                updateLoader.Start();
                if (effectiveType == UpdateEnums.UpdateType.UpdateNow)
                {
                    Loading.LoaderTaskbarAdd(updateLoader);
                    Launcher.frmMain.BtnExtraDownload.ShowRefresh();
                    Launcher.frmMain.BtnExtraDownload.Ribble();
                }
            }
            catch (Exception ex)
            {
                Common.Log(ex, "[Update] 获取启动器更新失败");
                if (effectiveType != UpdateEnums.UpdateType.Silent)
                    HintService.Show(Hints.Error(Lang.Text("Update.Error.FetchFailed")));
            }
        });
    }

    public static void UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true)
    {
        try
        {
            var fileName = Common.ExePath + @"PCL\Plain Craft Launcher Community Edition.exe";
            if (!File.Exists(fileName))
            {
                Common.Log("[System] 更新失败：未找到更新文件");
                return;
            }

            // id old new restart
            var text =
                $"update {Process.GetCurrentProcess().Id} \"{Basics.ExecutablePath}\" \"{fileName}\" {(triggerRestart ? "true" : "false")}";
            Common.Log("[System] 更新程序启动，参数：" + text);
            Process.Start(new ProcessStartInfo(fileName)
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, Arguments = text });
            if (triggerRestartAndByEnd)
            {
                Launcher.frmMain.EndProgram(false, true);
                Common.Log("[System] 已由于更新强制结束程序");
            }
        }
        catch (Win32Exception ex)
        {
            Common.Log(ex, "自动更新时触发 Win32 错误，疑似被拦截");
            DialogService.Instance.Show(new TextDialogRequest
            {
                Title = Lang.Text("Update.Error.UpdateBlocked"),
                Content = Lang.Text("Update.Error.UpdateBlockedMessage", Common.ExePath),
                Buttons = DialogButtons.Ok,
                IsWarn = true,
                ForceWait = true
            });
        }
    }

    /// <summary>
    ///     确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ///     如果不是，则下载一个。
    /// </summary>
    internal static void DownloadLatestPCL(Loading.LoaderBase loaderToSyncProgress = null)
    {
        // 注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
        var latestPCLPath = Path.Combine(Common.pathTemp, "CE-Latest.exe");
        var target = remoteServer.GetLatestVersion(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
        if (target is null)
            throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (File.Exists(latestPCLPath) &&
            (string.IsNullOrWhiteSpace(target.Sha256) ||
             (Common.GetFileSha256(latestPCLPath) ?? "") == (target.Sha256 ?? "")))
        {
            Common.Log("[System] 最新版 PCL 已存在，跳过下载");
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.Sha256) &&
            (Common.GetFileSha256(Basics.ExecutablePath) ?? "") ==
            (target.Sha256 ?? "")) // 正在使用的版本符合要求，直接拿来用
        {
            Common.CopyFile(Basics.ExecutablePath, latestPCLPath);
            return;
        }

        var loaders = remoteServer.GetDownloadLoader(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, latestPCLPath);
        var loader =
            new Loading.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders);
        loader.Start();
        loader.WaitForExit();
    }

    public static Loading.LoaderTask<int, int> serverLoader =
        new(Lang.Text("Update.Service.PclCe"),
            _ => LoadOnlineInfo(),
            priority: ThreadPriority.BelowNormal);

    private static void LoadOnlineInfo()
    {
        if (!OnlineServiceEnabled)
        {
            Common.Log("[Update] 在线服务已禁用，跳过自动更新检查与公告加载");
            return;
        }

        ScheduleBasedOnConfig();
        AnnouncementService.Load();
    }

    private static void ScheduleBasedOnConfig()
    {
        switch (Config.Update.UpdateMode)
        {
            case LauncherAutoUpdateBehavior.DownloadAndInstall:
                Common.Log("[Update] 更新设置: 自动下载并安装更新");
                if (GetVersionStatus() != UpdateEnums.VersionStatus.Latest)
                    UpdateStart(UpdateEnums.UpdateType.Silent);
                break;
            case LauncherAutoUpdateBehavior.DownloadAndAnnounce:
                Common.Log("[Update] 更新设置: 自动下载并提示更新");
                UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt);
                break;
            case LauncherAutoUpdateBehavior.AnnounceOnly:
                Common.Log("[Update] 更新设置: 提示更新");
                UpdateStart(UpdateEnums.UpdateType.PromptOnly);
                break;
            default:
                Common.Log("[Update] 更新设置: 不自动检查更新");
                // 强制更新不受「不自动检查更新」设置影响
                CheckForceUpdate();
                return;
        }
    }

    /// <summary>
    ///     仅执行强制更新检查：不打扰普通用户，
    ///     但命中云端「是否强制更新」或「强制最低版本」时必须更新。
    /// </summary>
    private static void CheckForceUpdate()
    {
        try
        {
            var version = remoteServer.GetLatestVersion(
                IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
            if (!(SemVer.Parse(version.VersionName) > SemVer.Parse(Common.VersionBaseName)))
                return;
            if (!IsForceRequired(version))
                return;
            Common.Log("[Update] 命中强制更新，不受「不自动检查更新」设置影响");
            UpdateStart(UpdateEnums.UpdateType.PromptOnly);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Update] 强制更新检查失败");
        }
    }
}
