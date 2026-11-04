using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PCL.Core.App.Localization;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Dialogs;

namespace PCL;

public partial class MyMsgText
{
    private readonly DialogViewModel myDialog;
    private readonly int uuid = Common.GetUuid();

    public MyMsgText(DialogViewModel dialog)
    {
        try
        {
            InitializeComponent();
            AppendUniqueNameSuffix(Btn1);
            AppendUniqueNameSuffix(Btn2);
            AppendUniqueNameSuffix(Btn3);
            myDialog = dialog;
            LabTitle.Text = dialog.Title;
            LabCaption.Text = dialog.TextContent;
            ConfigurePrimaryButton(dialog.ButtonText(1), dialog.IsWarn);
            ConfigureSecondaryButton(Btn2, dialog.ButtonText(2));
            ConfigureSecondaryButton(Btn3, dialog.ButtonText(3));
            ShapeLine.StrokeThickness = Common.GetWpfSize(1d);
        }

        catch (Exception ex)
        {
            Common.Log(
                ex,
                "普通弹窗初始化失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }

        Loaded += Load;
    }

    private void AppendUniqueNameSuffix(FrameworkElement element)
    {
        element.Name += Common.GetUuid();
    }

    private void ConfigurePrimaryButton(string text, bool isWarn)
    {
        Btn1.Text = text;
        if (isWarn)
        {
            Btn1.ColorType = MyButton.ColorState.Red;
            LabTitle.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedLight");
        }
    }

    private static void ConfigureSecondaryButton(MyButton button, string text)
    {
        button.Text = text;
        button.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Load(object sender, RoutedEventArgs e)
    {
        try
        {
            // UI 初始化
            if (Btn2.IsVisible && !(Btn1.ColorType == MyButton.ColorState.Red))
                Btn1.ColorType = MyButton.ColorState.Highlight;
            Btn1.Focus();
            // 动画
            Opacity = 0d;
            Animations.AniStart(
                Animations.AaColor(Launcher.frmMain.PanMsgBackground, BlurBorder.BackgroundProperty,
                    (myDialog.IsWarn
                        ? new Common.MyColor(140d, 80d, 0d, 0d)
                        : new Common.MyColor(90d, 0d, 0d, 0d)) -
                    Launcher.frmMain.PanMsgBackground.Background, 200),
                "PanMsgBackground Background");
            Animations.AniStart(
                new[]
                {
                    Animations.AaOpacity(this, 1d, 120, 60),
                    Animations.AaDouble(i => TransformPos.Y += (double)i,
                        -TransformPos.Y, 300, 60,
                        new Animations.AniEaseOutBack(Animations.AniEasePower.Weak)),
                    Animations.AaDouble(i => TransformRotate.Angle += (double)i,
                        -TransformRotate.Angle, 300, 60,
                        new Animations.AniEaseOutFluent(Animations.AniEasePower.Weak))
                }, "MyMsgBox " + uuid);
            // 记录日志
            Common.Log("[Control] 普通弹窗：" + LabTitle.Text + "\r\n" + LabCaption.Text);
        }

        catch (Exception ex)
        {
            Common.Log(
                ex,
                "普通弹窗加载失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
    }

    private void Close()
    {
        // 结束线程阻塞
        // 动画
        Animations.AniStart(new[]
        {
            Animations.AaCode(() =>
            {
                if (!PCL.Core.UI.Dialogs.DialogService.Instance.IsShowing && !PCL.Core.UI.Dialogs.DialogService.Instance.HasPending)
                    Animations.AniStart(Animations.AaColor(Launcher.frmMain.PanMsgBackground,
                        BlurBorder.BackgroundProperty,
                        new Common.MyColor(0d, 0d, 0d, 0d) -
                        Launcher.frmMain.PanMsgBackground.Background, 200,
                        ease: new Animations.AniEaseOutFluent(Animations.AniEasePower.Weak)));
            }, 30),
            Animations.AaOpacity(this, -Opacity, 80, 20),
            Animations.AaDouble(i => TransformPos.Y += (double)i, 20d - TransformPos.Y,
                150, 0, new Animations.AniEaseOutFluent()),
            Animations.AaDouble(i => TransformRotate.Angle += (double)i,
                6d - TransformRotate.Angle, 150, 0,
                new Animations.AniEaseInFluent(Animations.AniEasePower.Weak)),
            Animations.AaCode(() => ((Grid)Parent).Children.Remove(this), after: true)
        }, "MyMsgBox " + uuid);
    }

    public void Btn1_Click(object? sender = null, MouseButtonEventArgs? e = null) => ExecuteButton(1);

    public void Btn2_Click(object sender, MouseButtonEventArgs e) => ExecuteButton(2);

    public void Btn3_Click(object sender, MouseButtonEventArgs e) => ExecuteButton(3);

    /// <summary>
    ///     统一的按钮处理：执行动作后关闭弹窗，除非该按钮显式要求保留（例如 EULA 的「查看」）。
    ///     <para>
    ///         此前带动作的按钮执行完直接 return，弹窗既不会关闭、也不会走「关闭」分支；
    ///         公告弹窗正是「按钮1 + 空 + 关闭」的组合，一旦点掉唯一可见的按钮就再也关不掉了。
    ///     </para>
    /// </summary>
    private void ExecuteButton(int button)
    {
        if (myDialog.IsClosed) return;

        var action = myDialog.ButtonAction(button);
        if (action is null)
        {
            myDialog.CloseByButton(button);
            Close();
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                "弹窗按钮动作执行失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
        finally
        {
            if (!myDialog.ButtonKeepOpen(button) && !myDialog.IsClosed)
            {
                myDialog.CloseByButton(button);
                Close();
            }
        }
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                if (e.GetPosition(ShapeLine).Y <= 2d)
                    Launcher.frmMain.DragMove();
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                "拖拽移动失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
    }
}
