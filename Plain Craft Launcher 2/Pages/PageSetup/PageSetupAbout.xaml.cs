using System.Windows;
using System.Windows.Input;

namespace PCL;

public partial class PageSetupAbout : IPageView
{
    private readonly PageSetupAboutViewModel _vm = new();

    public PageSetupAbout()
    {
        InitializeComponent();
        DialogServiceInstaller.Install(_vm);
        Loaded += PageAbout_Loaded;
    }

    public PageSetupAboutViewModel ViewModel => _vm;

    public void OnNavigatedTo(object? parameter) => Refresh();

    public void OnNavigatedFrom()
    {
    }

    private void PageAbout_Loaded(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        PanBack.ScrollToHome();
    }

    private void ImgPCLCommunity_Click(object sender, MouseButtonEventArgs e)
    {
        _vm.RotateCommunityLogoCommand.Execute(sender);
    }

    private void ImgPCLLogo_Click(object sender, MouseButtonEventArgs e)
    {
        _vm.LogoClickCommand.Execute(sender);
    }
}
