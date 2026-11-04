using System;
using System.Windows;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     联机页左侧导航。P2P 直连页已从导航中撤下（页面代码保留在 <see cref="PageLinkP2P" />），
///     目前只暴露中继联机；需要恢复时把 PageLinkLeft.xaml 里注释掉的 ItemP2P 加回来即可。
/// </summary>
public partial class PageLinkLeft : IPageView
{
    private bool _isLoad;
    private bool _isPageSwitched;

    /// <summary>当前选中的子页面。</summary>
    public FormMain.PageSubType pageID = FormMain.PageSubType.LinkRelay;

    public PageLinkLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += (_, _) => RefreshNavigation();
        Unloaded += (_, _) => _isPageSwitched = false;
    }

    public void OnNavigatedTo(object? parameter) => RefreshNavigation();

    public void OnNavigatedFrom()
    {
    }

    private void RefreshNavigation()
    {
        if (_isLoad || _isPageSwitched) return;
        _isLoad = true;
        ItemRelay.SetChecked(true, false, false);
    }

    private void PageCheck(object senderRaw, Common.RouteEventArgs e)
    {
        var sender = (MyListItem)senderRaw;
        if (sender.Tag is not null)
            PageChange((FormMain.PageSubType)Common.Val(sender.Tag));
    }

    public object PageGet(FormMain.PageSubType? id = null)
    {
        switch (id ?? pageID)
        {
            case FormMain.PageSubType.LinkP2P:
            {
                Launcher.frmLinkP2P ??= new PageLinkP2P();
                return Launcher.frmLinkP2P;
            }
            case FormMain.PageSubType.LinkRelay:
            {
                Launcher.frmLinkRelay ??= new PageLinkRelay();
                return Launcher.frmLinkRelay;
            }
            default:
            {
                throw new Exception("未知的联机子页面种类：" + (int?)id);
            }
        }
    }

    public void PageChange(FormMain.PageSubType id)
    {
        // P2P 页已从导航中撤下，任何指向它的切换都回落到中继联机，避免出现没有导航项的空白状态
        if (id == FormMain.PageSubType.LinkP2P) id = FormMain.PageSubType.LinkRelay;
        if (pageID == id) return;
        Animations.AniBeginControl();
        _isPageSwitched = true;
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            pageID = id;
        }
        catch (Exception ex)
        {
            Common.Log(ex, $"切换联机子页面失败（ID {(int)id}）", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Tools.Error.OperationFailed"));
        }
        finally
        {
            Animations.AniEndControl();
        }
    }

    private static void PageChangeRun(MyPageRight target)
    {
        Animations.AniStop("FrmMain PageChangeRight");
        if (target.Parent is not null)
            target.SetValue(System.Windows.Controls.ContentPresenter.ContentProperty, null);
        Launcher.frmMain.pageRight = target;
        ((MyPageRight)Launcher.frmMain.PanMainRight.Child).PageOnExit();
        Animations.AniStart(new[]
        {
            Animations.AaCode(() =>
            {
                ((MyPageRight)Launcher.frmMain.PanMainRight.Child).PageOnForceExit();
                Launcher.frmMain.PanMainRight.Child = Launcher.frmMain.pageRight;
                Launcher.frmMain.pageRight.Opacity = 0d;
            }, 130),
            Animations.AaCode(() =>
            {
                Launcher.frmMain.pageRight.Opacity = 1d;
                Launcher.frmMain.pageRight.PageOnEnter();
            }, 30, true)
        }, "PageLeft PageChange");
    }
}
