using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Core.App.Localization;
using PCL.Core.UI.Dialogs;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupUpdate : IPageView
{
    private readonly PageSetupUpdateViewModel _vm = new();
    private bool _refreshed;

    public VersionDataModel updateInfo;

    public PageSetupUpdate()
    {
        InitializeComponent();
        DataContext = _vm;
        DialogServiceInstaller.Install(_vm);
        Loaded += (_, _) => Refresh();
        Unloaded += PageSetupUpdate_Unloaded;
    }

    public PageSetupUpdateViewModel ViewModel => _vm;

    public void OnNavigatedTo(object? parameter) => Refresh();

    public void OnNavigatedFrom()
    {
    }

    private void PageSetupUpdate_Unloaded(object sender, RoutedEventArgs e) => _refreshed = false;

    private void Refresh()
    {
        if (_refreshed)
            return;
        _refreshed = true;
        Init();
    }

    private void Init()
    {
        Animations.AniBeginControl();

        // beta 更新渠道已移除，固定使用正式版
        Config.Update.UpdateChannel = Core.App.UpdateChannel.Release;
        ComboSystemUpdateChannel.SelectedIndex = 0;
        ComboSystemUpdateMode.SelectedIndex = (int)Config.Update.UpdateMode;

        _vm.Reload();
        Animations.AniEndControl();

        // 进入页面时自动检查一次更新（更新源为自建云端 updateinfo.json）
        CheckUpdate();
    }

    private async Task<UpdateStatus> IsLatestAsync()
    {
        try
        {
            // 修复：使用 dynamic 绕过命名空间重名导致的编译期类型冲突，
            // 或者你可以尝试替换为 PCL.Core.App.SemVer.Parse(Common.VersionBaseName)
            if (await UpdateManager.remoteServer.IsLatestAsync(
                    UpdateManager.IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64,
                    SemVer.Parse(Common.VersionBaseName),
                    Common.VersionCode))
            {
                Common.Log("[Update] 已是最新版本");
                return UpdateStatus.Latest;
            }

            Common.Log("[Update] 有可用的新版本");
            return UpdateStatus.Available;
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                Lang.Text("Setup.Update.Error.NetworkFailed"),
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
            return UpdateStatus.Error;
        }
    }

    public async void CheckUpdate()
    {
        Common.Log("[Update] 开始检查更新");
        _vm.ShowUpdateCard = false;
        _vm.ShowCheckCard = true;
        _vm.CurrentDescText = Lang.Text("Setup.Update.Checking");
        _vm.CheckAgainEnabled = false;
        switch (await IsLatestAsync())
        {
            case UpdateStatus.Available:
            {
                Exception checkUpdateEx = null;
                try
                {
                    // 版本信息可能还没缓存（启动检查未完成），GetLatestVersion 会同步发请求，
                    // 必须放到后台线程，否则网络慢时整个设置页会卡住不响应。
                    updateInfo = await Task.Run(() => UpdateManager.remoteServer.GetLatestVersion(
                        UpdateManager.IsCurrentVersionBeta
                            ? UpdateChannel.beta
                            : UpdateChannel.stable, SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64));
                    _vm.UpdateNameText = "PCLCE-NS " + VersionNameFormat(updateInfo.VersionName);
                    // 云端更新日志为纯文本/Markdown；旧清单格式用 <summary> 包裹摘要
                    var changelog = updateInfo.Changelog ?? "";
                    if (changelog.Contains("<summary>"))
                    {
                        var summary = changelog.Between("<summary>", "</summary>");
                        _vm.ChangelogText = string.IsNullOrWhiteSpace(summary.Trim())
                            ? Lang.Text("Setup.Update.Changelog.Empty")
                            : summary;
                    }
                    else
                    {
                        _vm.ChangelogText = string.IsNullOrWhiteSpace(changelog.Trim())
                            ? Lang.Text("Setup.Update.Changelog.Empty")
                            : changelog;
                    }
                }
                catch (Exception ex)
                {
                    checkUpdateEx = ex;
                }

                _vm.CheckAgainEnabled = true;
                if (updateInfo is null)
                {
                    _vm.CurrentDescText = Lang.Text("Setup.Update.CheckFailed");
                    if (checkUpdateEx is not null)
                        Common.Log(
                            checkUpdateEx,
                            "[Update] 检查更新失败",
                            Common.LogLevel.Msgbox,
                            userSummary: Lang.Text("Update.Check.Failed"));
                    else
                        Common.Log(
                            "[Update] 检查更新失败",
                            Common.LogLevel.Msgbox,
                            userSummary: Lang.Text("Update.Check.Failed"));
                    return;
                }

                if (UpdateManager.updateLoader is not null && UpdateManager.updateLoader.State == Common.LoadState.Loading)
                {
                    BtnUpdate_Timer();
                    BtnUpdate.IsEnabled = false;
                }
                else if (UpdateManager.isUpdateWaitingRestart)
                {
                    BtnUpdate.Text = Lang.Text("Setup.Update.RestartInstall");
                    BtnUpdate.IsEnabled = true;
                }
                else
                {
                    BtnUpdate.Text = Lang.Text("Setup.Update.Install");
                    BtnUpdate.IsEnabled = true;
                }

                _vm.ShowUpdateCard = true;
                _vm.ShowCheckCard = false;
                break;
            }
            case UpdateStatus.Latest:
            {
                _vm.ShowUpdateCard = false;
                _vm.ShowCheckCard = true;
                _vm.CheckAgainEnabled = true;
                _vm.CurrentDescText = Lang.Text("Setup.Update.Latest");
                break;
            }
            case UpdateStatus.Error:
            {
                _vm.ShowUpdateCard = false;
                _vm.ShowCheckCard = true;
                _vm.CheckAgainEnabled = true;
                _vm.CurrentDescText = Lang.Text("Setup.Update.CheckFailed");
                break;
            }
        }
    }

    /// <summary>
    ///     下载进度轮询。这里必须用 await Task.Delay 而不是 Thread.Sleep：
    ///     后者会占死 UI 线程，下载期间整页（包括窗口本身）完全无响应。
    /// </summary>
    public async void BtnUpdate_Timer()
    {
        while (UpdateManager.updateLoader is { State: Common.LoadState.Loading })
        {
            BtnUpdate.Text = Lang.Number(UpdateManager.updateLoader.Progress, "P2");
            await Task.Delay(200);
        }
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (UpdateManager.isUpdateWaitingRestart) UpdateManager.UpdateRestart(true);
        // 开始更新流程
        UpdateManager.UpdateStart(UpdateEnums.UpdateType.UpdateNow);
    }

    private async void BtnChangelogDetail_Click(object sender, EventArgs e)
    {
        _vm.ChangelogDetailEnabled = false;
        try
        {
            if (updateInfo is null)
                await _vm.Dialog.ShowAsync(new TextDialogRequest
                {
                    Title = Lang.Text("Setup.Update.Changelog.Title"),
                    Content = Lang.Text("Setup.Update.Changelog.Unavailable"),
                    Buttons = DialogButtons.Ok
                });
            else
                _vm.Dialog.Show(new MarkdownDialogRequest
                {
                    Title = Lang.Text("Setup.Update.Changelog.Title"),
                    Content = updateInfo.Changelog ?? "",
                    Buttons = DialogButtons.Ok,
                    ForceWait = true
                });
        }
        catch (Exception ex)
        {
            Common.Log(ex, "查看更新日志失败", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.Error.OperationFailed"));
        }
        finally
        {
            _vm.ChangelogDetailEnabled = true;
        }
    }

    private void ComboSystemUpdateMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!Animations.AniExitsAnyControl())
            Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)ComboSystemUpdateMode.SelectedIndex;
    }

    private async void BtnChangelog_Click(object sender, MouseButtonEventArgs e)
    {
        // 更新日志改为从云端获取（apiv2/updateinfo.json 的 changelog 字段），不再跳转 GitHub
        BtnChangelog.IsEnabled = false;
        try
        {
            var changelog = await Task.Run(() => UpdateManager.FetchCloudChangelog());
            if (string.IsNullOrWhiteSpace(changelog))
                await _vm.Dialog.ShowAsync(new TextDialogRequest
                {
                    Title = Lang.Text("Setup.Update.Changelog.Title"),
                    Content = Lang.Text("Setup.Update.Changelog.Unavailable"),
                    Buttons = DialogButtons.Ok
                });
            else
                _vm.Dialog.Show(new MarkdownDialogRequest
                {
                    Title = Lang.Text("Setup.Update.Changelog.Title"),
                    Content = changelog,
                    Buttons = DialogButtons.Ok,
                    ForceWait = true
                });
        }
        catch (Exception ex)
        {
            Common.Log(ex, "获取云端更新日志失败", Common.LogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
        }
        finally
        {
            BtnChangelog.IsEnabled = true;
        }
    }

    public string VersionNameFormat(string str) => PageSetupUpdateViewModel.VersionNameFormat(str);

    private void BtnCheckAgain_OnClick(object sender, MouseButtonEventArgs e)
    {
        CheckUpdate();
    }

    private enum UpdateStatus
    {
        Checking = 0,
        Available = 1,
        Error = 2,
        Latest = 3
    }
}
