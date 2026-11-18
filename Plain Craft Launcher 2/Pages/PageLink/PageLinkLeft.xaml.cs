using System;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.Link.Relay;

namespace PCL;

/// <summary>
///     联机页左侧导航，提供 P2P 打洞与中继联机。
/// </summary>
public partial class PageLinkLeft : IPageView
{
    private bool _isLoad;
    private bool _isPageSwitched;
    private bool _fetching;
    private bool _applying;
    private bool _p2pEnabled = true;
    private bool _relayEnabled = true;
    private readonly System.Windows.Threading.DispatcherTimer _policyTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly MyPageRight _unavailable = new()
    {
        Child = new System.Windows.Controls.TextBlock
        {
            Text = "联机功能已由管理员关闭。", Margin = new Thickness(25, 30, 25, 20),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7
        }
    };

    /// <summary>当前选中的子页面。</summary>
    public FormMain.PageSubType pageID = FormMain.PageSubType.LinkRelay;

    public PageLinkLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += (_, _) => { RefreshNavigation(); _policyTimer.Start(); };
        Unloaded += (_, _) => { _isPageSwitched = false; _policyTimer.Stop(); };
        _policyTimer.Tick += (_, _) => { if (IsVisible) RefreshNavigation(); };
    }

    public void OnNavigatedTo(object? parameter) => RefreshNavigation();

    public void OnNavigatedFrom()
    {
    }

    private async void RefreshNavigation()
    {
        if (_fetching) return;
        _fetching = true;
        try
        {
            var status = await new RelayApi().GetStatusAsync();
            _p2pEnabled = status.P2pEnabled;
            _relayEnabled = status.RelayEnabled;
        }
        catch (Exception) { }
        finally { _fetching = false; }
        _applying = true;
        ItemP2P.Visibility = _p2pEnabled ? Visibility.Visible : Visibility.Collapsed;
        ItemRelay.Visibility = _relayEnabled ? Visibility.Visible : Visibility.Collapsed;
        LabUnavailable.Visibility = !_p2pEnabled && !_relayEnabled ? Visibility.Visible : Visibility.Collapsed;
        _applying = false;
        if (!_p2pEnabled && !_relayEnabled)
        {
            if (IsLoaded) PageChangeRun(_unavailable);
            return;
        }
        var desired = pageID;
        if (desired == FormMain.PageSubType.LinkP2P && !_p2pEnabled) desired = FormMain.PageSubType.LinkRelay;
        else if (desired == FormMain.PageSubType.LinkRelay && !_relayEnabled) desired = FormMain.PageSubType.LinkP2P;
        if (desired != pageID || ReferenceEquals(Launcher.frmMain.pageRight, _unavailable))
        {
            pageID = desired;
            if (IsLoaded) PageChangeRun((MyPageRight)PageGet(desired));
        }
        _isLoad = true;
        var item = pageID == FormMain.PageSubType.LinkP2P ? ItemP2P : ItemRelay;
        item.SetChecked(true, false, false);
    }

    private void PageCheck(object senderRaw, Common.RouteEventArgs e)
    {
        if (_applying) return;
        var sender = (MyListItem)senderRaw;
        if (sender.Tag is not null)
            PageChange((FormMain.PageSubType)Common.Val(sender.Tag));
    }

    public object PageGet(FormMain.PageSubType? id = null)
    {
        if (!_p2pEnabled && !_relayEnabled) return _unavailable;
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
        if (id == FormMain.PageSubType.LinkP2P && !_p2pEnabled || id == FormMain.PageSubType.LinkRelay && !_relayEnabled) return;
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
