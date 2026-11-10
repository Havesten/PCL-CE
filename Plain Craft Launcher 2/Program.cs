using System.Diagnostics;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;

namespace PCL;

internal static class Program
{
    /// <summary>
    /// Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        // 自更新落地：本进程若由更新流程以更新器身份启动，只做「等旧进程退出 → 覆盖 exe → 重启」，
        // 不初始化任何界面。必须放在最前面，此时还没有任何文件句柄占用自身。
        if (UpdateApplier.TryApply()) return;

        if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();
#if DEBUG
        if (Basics.CommandLineArguments.Contains("--debug"))
        {
            Console.WriteLine("Waiting for debugger...");
            while (!Debugger.IsAttached) Thread.Sleep(50);
        }
#endif
        Console.WriteLine("Welcome to PCLCE-NS!");
        // Preloading tasks
        ApplicationService.Loading = static () =>
        {
            var app = new Application();
            app.InitializeComponent();
            return app;
        };
        MainWindowService.Loading = static () =>
        {
            var form = new FormMain();
            return form;
        };
        // 注册 Core 弹窗服务的界面呈现器
        DialogPresenter.Install();
        HintPresenter.Install();
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+

        _ = Tablet.TabletDevices;
        // Start lifecycle
        Lifecycle.OnInitialize();
    }
}