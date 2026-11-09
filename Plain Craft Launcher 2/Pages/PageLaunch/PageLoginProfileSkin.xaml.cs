using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL;

public partial class PageLoginProfileSkin : IPageView
{
    private readonly PageLoginProfileSkinViewModel _vm = new();

    private bool _refreshed;

    public PageLoginProfileSkin()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += (_, _) => RefreshOnNavigate();
        Unloaded += (_, _) => _refreshed = false;
        // Handles
    }

    public void OnNavigatedTo(object? parameter)
    {
        RefreshOnNavigate();
    }

    public void OnNavigatedFrom()
    {
    }

    private void RefreshOnNavigate()
    {
        if (_refreshed)
            return;
        _refreshed = true;
        Reload();
    }

    public PageLoginProfileSkinViewModel ViewModel => _vm;

    /// <summary>
    ///     刷新页面显示的所有信息。
    /// </summary>
    public void Reload()
    {
        Common.Log("[Profile] 刷新档案界面");
        Skin.Clear();
        var profile = ProfileService.Current ?? throw new InvalidOperationException("No profile selected.");
        Common.Log("[Profile] 使用正版皮肤加载器");
        Skin.loader = PageLaunchLeft.skinMs;

        Skin.loader.Start(isForceRestart: true);
        _vm.Load(profile);
    }

    #region 控制与编辑

    // 显示 / 隐藏控制
    private void ShowPanel(object sender, MouseEventArgs e)
    {
        Animations.AniStart(Animations.AaOpacity(PanButtons, 1d - PanButtons.Opacity, 120),
            "PageLoginProfileSkin Button");
    }

    private void HidePanel(object sender, MouseEventArgs? e)
    {
        if (BtnEdit.ContextMenu.IsOpen || BtnSkin.ContextMenu.IsOpen || PanData.IsMouseOver)
            return;
        Animations.AniStart(Animations.AaOpacity(PanButtons, -PanButtons.Opacity, 120),
            "PageLoginProfileSkin Button");
    }

    private void MenuAccountOptions_Closed(object sender, RoutedEventArgs e)
    {
        HidePanel(sender, null);
    }

    // 皮肤与披风子菜单
    private void BtnSkin_Click(object sender, EventArgs e)
    {
        BtnSkin.ContextMenu.IsOpen = true;
    }

    // 账号信息子菜单
    private void BtnEdit_Click(object sender, EventArgs e)
    {
        BtnEdit.ContextMenu.IsOpen = true;
    }

    // 修改密码
    private void BtnEditPassword_Click(object sender, RoutedEventArgs e)
    {
        Common.OpenWebsite("https://account.live.com/password/Change");
    }

    // 修改 ID
    private void BtnEditName_Click(object sender, RoutedEventArgs e)
    {
        ProfileUi.EditProfileId();
    }

    // 选择档案
    private void ChangeProfile(object sender, EventArgs e)
    {
        ProfileService.Select(null);
        Common.RunInUi(() =>
        {
            Launcher.frmLaunchLeft.RefreshPage(true);
            Launcher.frmLaunchLeft.BtnLaunch.IsEnabled = false;
        });
    }

    // 修改皮肤
    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        ProfileUi.ChangeSkinMs();
    }

    // 保存皮肤
    private void BtnSkinSave_Click(object sender, RoutedEventArgs e)
    {
        Skin.BtnSkinSave_Click(sender, e);
    }

    // 刷新皮肤
    private void BtnSkinRefresh_Click(object sender, RoutedEventArgs e)
    {
        Skin.RefreshClick(sender, e);
    }

    // 修改披风
    private void BtnSkinCape_Click(object sender, RoutedEventArgs e)
    {
        Skin.BtnSkinCape_Click(sender, e);
    }

    #endregion
}
