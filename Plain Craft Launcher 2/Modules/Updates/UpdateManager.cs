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

    private static UpdateEnums.VersionStatus _cachedStatus = UpdateEnums.VersionStatus.Unknown;
    private static DateTime _cachedStatusAt = DateTime.MinValue;

    /// <summary>
    ///     最近一次算出的版本状态，只读缓存、不发请求。
    ///     <para>
    ///         界面上的「是否支持反馈」「关于页版本提示」都用它：这些地方在 UI 线程执行，
    ///         直接调 <see cref="GetVersionStatus" /> 会在网络慢时把界面卡住几十秒。
    ///         缓存由启动时的后台检查填充。
    ///     </para>
    /// </summary>
    public static UpdateEnums.VersionStatus CachedVersionStatus => _cachedStatus;

    /// <summary>缓存时间，用于判断是否需要重新计算。</summary>
    public static DateTime CachedVersionStatusAt => _cachedStatusAt;

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
        // 用户主动点「查看更新日志」，绕开 60 秒缓存，保证看到的是刚发布的版本
        _cloudSource.RefreshCache(true);
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
        var status = ComputeVersionStatus();
        _cachedStatus = status;
        _cachedStatusAt = DateTime.Now;
        return status;
    }

    /// <summary>
    ///     真正发请求算版本状态。调用方必须在后台线程，或接受被网络阻塞的风险；
    ///     UI 线程请改用 <see cref="CachedVersionStatus" />。
    /// </summary>
    private static UpdateEnums.VersionStatus ComputeVersionStatus()
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

    /// <summary>
    ///     下载好的新版本临时位置。放在 PCL\Update\ 下而不是安装目录根，
    ///     既不会和用户手动放的 exe 混淆，也和目标文件同盘，替换时是移动而非跨盘拷贝。
    /// </summary>
    public static string UpdatePackagePath =>
        Path.Combine(Common.ExePath, "PCL", "Update", "PCLCE-NS-update.exe");

    /// <summary>自替换器副本。由当前进程复制自身得到，不随版本变化。</summary>
    private static string UpdaterPath => Path.Combine(Common.ExePath, "PCL", "Update", "Updater.exe");

    public static void UpdateStart(UpdateEnums.UpdateType type, string receivedKey = null, bool forceValidated = false)
    {
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

                // 强制更新（云端「是否强制更新」或低于「强制最低版本」）不允许跳过，
                // 也不弹询问框：直接下载 → 替换 → 重启，用户下次看到的就是新版本。
                if (IsForceRequired(version))
                {
                    Common.Log("[Update] 命中强制更新，跳过询问直接下载并替换");
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

                var dlTargetPath = UpdatePackagePath;
                var dlDir = Path.GetDirectoryName(dlTargetPath);
                if (!string.IsNullOrEmpty(dlDir)) Directory.CreateDirectory(dlDir);

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
            var source = UpdatePackagePath;
            if (!File.Exists(source))
            {
                Common.Log("[System] 更新失败：未找到已下载的更新文件 " + source);
                return;
            }

            var updater = PrepareUpdater();
            // 由更新器完成真正的替换：它等本进程退出后才动手，避免覆盖正在运行的文件
            var args =
                $"--apply-update {Environment.ProcessId} \"{Basics.ExecutablePath}\" \"{source}\" {(triggerRestart ? "1" : "0")}";
            Common.Log("[System] 启动自更新器，参数：" + args);
            Process.Start(new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(updater) ?? Common.ExePath,
                Arguments = args
            });
            if (triggerRestartAndByEnd) ShutdownForUpdate();
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
    ///     准备自更新器：把当前 exe 复制一份到 PCL\Update\ 下。
    ///     更新器必须是「当前版本」的副本，这样它的 --apply-update 逻辑一定存在，
    ///     不依赖新版本是否支持该参数。
    /// </summary>
    private static string PrepareUpdater()
    {
        var updater = UpdaterPath;
        var dir = Path.GetDirectoryName(updater);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Basics.ExecutablePath, updater, true);
            return updater;
        }

        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 上一轮残留的更新器可能还被占用，换个名字绕开
            Common.Log(ex, "[System] 更新器副本被占用，改用临时名字");
            var alternative = Path.Combine(dir ?? Common.ExePath,
                $"Updater-{DateTime.Now:HHmmss}.exe");
            File.Copy(Basics.ExecutablePath, alternative, true);
            return alternative;
        }
    }

    /// <summary>更新前退出：优先走正常退出流程（保存配置），窗体还没建好时直接结束进程。</summary>
    private static void ShutdownForUpdate()
    {
        try
        {
            if (Launcher.frmMain is not null)
            {
                Launcher.frmMain.EndProgram(false, true);
                Common.Log("[System] 已由于更新强制结束程序");
                return;
            }
        }

        catch (Exception ex)
        {
            Common.Log(ex, "[System] 更新退出时出错，改为直接结束进程");
        }

        Environment.Exit(0);
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

        // 强制更新优先：命中就直接下载替换，不再等用户点按钮；
        // 此时也不需要再走常规的「提示/自动下载」调度，避免重复检查与弹窗打架。
        if (CheckForceUpdate()) return;

        // 把版本状态算一次放进缓存，界面（反馈入口、关于页）直接读缓存，
        // 不再为了显示一行文字在 UI 线程发同步请求。
        try
        {
            _cachedStatus = GetVersionStatus();
            _cachedStatusAt = DateTime.Now;
        }

        catch (Exception ex)
        {
            Common.Log(ex, "[Update] 预取版本状态失败");
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
                // 强制更新已在启动时优先检查过（见 LoadOnlineInfo），这里不再重复请求
                Common.Log("[Update] 更新设置: 不自动检查更新");
                return;
        }
    }

    /// <summary>
    ///     仅执行强制更新检查：不打扰普通用户，
    ///     但命中云端「是否强制更新」或「强制最低版本」时必须更新。
    ///     命中后会直接开始下载并替换，返回 true 表示已进入更新流程。
    /// </summary>
    private static bool CheckForceUpdate()
    {
        try
        {
            var version = remoteServer.GetLatestVersion(
                IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
            if (!(SemVer.Parse(version.VersionName) > SemVer.Parse(Common.VersionBaseName)))
                return false;
            if (!IsForceRequired(version))
                return false;
            Common.Log("[Update] 命中强制更新，不受「不自动检查更新」设置影响，开始静默下载替换");
            UpdateStart(UpdateEnums.UpdateType.UpdateNow);
            return true;
        }

        catch (Exception ex)
        {
            Common.Log(ex, "[Update] 强制更新检查失败");
            return false;
        }
    }
}
