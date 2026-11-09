using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using PCL.Core.Minecraft.IdentityModel.OAuth;

namespace PCL.Core.UI.Dialogs;

public enum DialogKind
{
    Text,
    Markdown,
    Input,
    Select,
    Login,
    PhoneBind
}

/// <summary>弹窗请求的公共部分。具体内容由各子类型提供。</summary>
public abstract record DialogRequest
{
    public required string Title { get; init; }

    public DialogButtons Buttons { get; init; } = DialogButtons.Ok;

    public bool IsWarn { get; init; }

    public bool HighLight { get; init; } = true;

    /// <summary>即使只有一个按钮也强制显示并等待。</summary>
    public bool ForceWait { get; init; }

    public abstract DialogKind Kind { get; }
}

public sealed record TextDialogRequest : DialogRequest
{
    public required string Content { get; init; }

    public override DialogKind Kind => DialogKind.Text;
}

public sealed record MarkdownDialogRequest : DialogRequest
{
    public required string Content { get; init; }

    public override DialogKind Kind => DialogKind.Markdown;
}

public sealed record InputDialogRequest : DialogRequest
{
    public string Content { get; init; } = "";

    public string DefaultValue { get; init; } = "";

    public string HintText { get; init; } = "";

    public IReadOnlyList<IValidator<string>> ValidateRules { get; init; } = [];

    public override DialogKind Kind => DialogKind.Input;
}

/// <summary>可选项。多选模式下 <see cref="IsChecked"/> 表示默认勾选。</summary>
public sealed record DialogOption
{
    public required string Text { get; init; }

    public string? Description { get; init; }

    public string? Image { get; init; }

    public double ImageScale { get; init; } = 1d;

    public bool IsChecked { get; init; }

    public object? Tag { get; init; }
}

public sealed record SelectDialogRequest : DialogRequest
{
    public required IReadOnlyList<DialogOption> Options { get; init; }

    public bool MultiSelect { get; init; }

    public override DialogKind Kind => DialogKind.Select;
}

public sealed record LoginDialogRequest : DialogRequest
{
    public required JsonObject Data { get; init; }

    public Func<JsonObject, CancellationToken, Task<AuthorizeResult?>>? DeviceCodePoll { get; init; }

    public Func<AuthorizeResult, CancellationToken, Task>? LoginResultHandler { get; init; }

    public Action<object?>? CompletionHandler { get; init; }

    public override DialogKind Kind => DialogKind.Login;
}

/// <summary>
///     绑定手机号弹窗。发码与绑定动作由调用方注入，弹窗只负责交互与本地号码校验，
///     避免 UI 层直接依赖具体业务接口。
/// </summary>
public sealed record PhoneBindDialogRequest : DialogRequest
{
    public string Content { get; init; } = "";

    /// <summary>取一道人机验证，返回 (id, 题目)。</summary>
    public required Func<Task<(string Id, string Prompt)>> GetCaptchaAsync { get; init; }

    /// <summary>发送短信验证码，返回开发模式下服务端回显的验证码（线上为 null）。</summary>
    public required Func<string, string, string, Task<string?>> SendSmsAsync { get; init; }

    /// <summary>提交绑定（手机号, 验证码）。</summary>
    public required Func<string, string, Task> BindAsync { get; init; }

    public override DialogKind Kind => DialogKind.PhoneBind;
}
