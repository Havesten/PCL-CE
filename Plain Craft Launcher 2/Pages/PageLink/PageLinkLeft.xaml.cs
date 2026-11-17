using System;
using System.Windows;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     联机页左侧导航，提供 P2P 直连与中继联机两种方式。
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
        var item = pageID == FormMain.PageSubType.LinkP2P ? ItemP2P : ItemRelay;
        item.SetChecked(true, false, false);
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
