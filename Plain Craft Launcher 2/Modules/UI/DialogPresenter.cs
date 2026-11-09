using System;
using System.Linq;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.UI.Dialogs;

namespace PCL;

public sealed class DialogPresenter : IDialogPresenter
{
    public static DialogPresenter Instance { get; } = new();

    public static void Install()
    {
        PCL.Core.UI.Dialogs.DialogService.Instance.Presenter = Instance;
    }

    public bool IsAvailable
    {
        get
        {
            var main = Launcher.frmMain;
            return main?.PanMsg is not null
                   && main.WindowState != WindowState.Minimized
                   && main.PanMsg.Children.Count == 0;
        }
    }

    public void Present(DialogViewModel dialog)
    {
        var main = Launcher.frmMain;
        if (main?.PanMsg is null || main.PanMsgBackground is null)
        {
            ShowFallback(dialog);
            return;
        }

        try
        {
            FrameworkElement? view = dialog.Kind switch
            {
                DialogKind.Text => new MyMsgText(dialog),
                DialogKind.Markdown => new MyMsgMarkdown(dialog),
                DialogKind.Input => new MyMsgInput(dialog),
                DialogKind.Select => new MyMsgSelect(dialog),
                DialogKind.Login => new MyMsgLogin(dialog),
                DialogKind.PhoneBind => new MyMsgPhoneBind(dialog),
                _ => null
            };

            if (view is null)
            {
                ShowFallback(dialog);
                return;
            }

            main.PanMsgBackground.Visibility = Visibility.Visible;
            main.PanMsg.Children.Add(view);
            view.Unloaded += (_, _) =>
            {
                if (main.PanMsg is { Children.Count: 0 })
                    main.PanMsgBackground.Visibility = Visibility.Collapsed;
            };
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                "弹窗显示失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
            dialog.Close(DialogResult.None);
        }
    }

    public void Dismiss(DialogViewModel dialog)
    {
        // 控件在退场动画结束后自行从 PanMsg 移除
    }

    public void ShowFallback(DialogViewModel dialog)
    {
        try
        {
            var text = string.IsNullOrEmpty(dialog.TextContent) ? dialog.Title : dialog.TextContent;
            // 中间可能留空（例如「按钮1 + 空 + 关闭」），按实际存在的按钮建立索引映射
            var indices = Enumerable.Range(1, DialogButtons.Capacity)
                .Where(index => !string.IsNullOrEmpty(dialog.ButtonText(index)))
                .ToArray();
            var flags = indices.Length switch
            {
                <= 1 => Microsoft.VisualBasic.MsgBoxStyle.OkOnly,
                2 => Microsoft.VisualBasic.MsgBoxStyle.YesNo,
                _ => Microsoft.VisualBasic.MsgBoxStyle.YesNoCancel
            };
            var icon = dialog.IsWarn
                ? Microsoft.VisualBasic.MsgBoxStyle.Exclamation
                : Microsoft.VisualBasic.MsgBoxStyle.Information;
            var result = Microsoft.VisualBasic.Interaction.MsgBox(text, flags | icon, dialog.Title);
            var position = result switch
            {
                Microsoft.VisualBasic.MsgBoxResult.Yes => 0,
                Microsoft.VisualBasic.MsgBoxResult.No => 1,
                Microsoft.VisualBasic.MsgBoxResult.Cancel => Math.Max(0, indices.Length - 1),
                _ => 0
            };
            var button = indices.Length == 0 ? 1 : indices[Math.Min(position, indices.Length - 1)];
            dialog.Close(new DialogResult(button));
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                "弹窗兜底显示失败",
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
            dialog.Close(DialogResult.None);
        }
    }
}
