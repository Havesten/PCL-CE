using System;

namespace PCL.Core.UI.Dialogs;

/// <summary>弹窗按钮。文本为空表示该位置没有按钮。</summary>
/// <param name="Text">按钮文本</param>
/// <param name="OnClick">点击时执行的动作，执行后默认仍会关闭弹窗</param>
/// <param name="KeepOpen">动作执行后是否保留弹窗（例如 EULA 的「查看」只是打开网页）</param>
public readonly record struct DialogButton(string? Text, Action? OnClick = null, bool KeepOpen = false)
{
    public bool IsEmpty => string.IsNullOrEmpty(Text);

    public static DialogButton None => default;

    public static DialogButton Create(string? text, Action? onClick = null, bool keepOpen = false)
        => new(text, onClick, keepOpen);
}
