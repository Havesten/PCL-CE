using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net;

namespace PCL.Core.Link.Relay;

/// <summary>
///     中继联机的云端 API 客户端。
///     所有请求都走启动器统一的 HttpClient，因此自定义代理、节点优选对它同样生效。
/// </summary>
public sealed class RelayApi
{
    /// <summary>默认的云端根地址。与更新源共用 <see cref="CloudEndpoints.ApiRoot" />，路径前缀 /api/v1。</summary>
    private const string DefaultBaseUrl = CloudEndpoints.ApiRoot;

    /// <summary>
    ///     云端根地址。设置环境变量 <c>PCL_RELAY_API</c> 可以指向本地或自建服务，便于联调。
    /// </summary>
    public static string BaseUrl { get; set; } = ResolveBaseUrl();

    private static string ResolveBaseUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PCL_RELAY_API");
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultBaseUrl : fromEnv.TrimEnd('/');
    }

    private readonly HttpClient _client = NetworkService.GetClient();

    /// <summary>
    ///     登录后由 <see cref="RelaySession" /> 注入，所有需鉴权的接口都要带上。
    /// </summary>
    public string? Token { get; set; }

    public async Task<RelayStatus> GetStatusAsync(CancellationToken ct = default) =>
        await SendAsync<RelayStatus>(HttpMethod.Get, "/api/v1/status", null, ct).ConfigureAwait(false);

    public async Task<RelayCaptcha> GetCaptchaAsync(CancellationToken ct = default) =>
        await SendAsync<RelayCaptcha>(HttpMethod.Post, "/api/v1/auth/captcha", new { }, ct).ConfigureAwait(false);

    /// <summary>
    ///     发送邮箱验证码。返回开发模式下服务端回显的验证码（线上为 null）。
    /// </summary>
    public async Task<string?> SendCodeAsync(string email, string captchaId, string captchaAnswer,
        CancellationToken ct = default)
    {
        var res = await SendAsync<RelaySendCodeResult>(HttpMethod.Post, "/api/v1/auth/send-code", new
        {
            email,
            captcha_id = captchaId,
            captcha_answer = captchaAnswer
        }, ct).ConfigureAwait(false);
        return res.DevCode;
    }

    public async Task<RelayLoginResult> LoginAsync(string email, string code, CancellationToken ct = default)
    {
        var res = await SendAsync<RelayLoginResult>(HttpMethod.Post, "/api/v1/auth/login", new
        {
            email,
            code
        }, ct).ConfigureAwait(false);
        Token = res.Token;
        return res;
    }

    public async Task<RelayMe> GetMeAsync(CancellationToken ct = default) =>
        await SendAsync<RelayMe>(HttpMethod.Get, "/api/v1/me", null, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RelayNode>> GetNodesAsync(CancellationToken ct = default)
    {
        var res = await SendAsync<RelayNodeList>(HttpMethod.Get, "/api/v1/relay/nodes", null, ct)
            .ConfigureAwait(false);
        return res.Nodes ?? [];
    }

    public async Task<RelayAllocationResult> AllocateAsync(string nodeId, string label, CancellationToken ct = default) =>
        await SendAsync<RelayAllocationResult>(HttpMethod.Post, "/api/v1/relay/allocations", new
        {
            node_id = nodeId,
            label
        }, ct).ConfigureAwait(false);

    public async Task ReleaseAsync(string allocationId, CancellationToken ct = default) =>
        await SendAsync<RelayQuota>(HttpMethod.Post,
            $"/api/v1/relay/allocations/release?id={Uri.EscapeDataString(allocationId)}", null, ct)
            .ConfigureAwait(false);

    // ---------------------------------------------------------------- 传输层

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (!string.IsNullOrWhiteSpace(Token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // 对外只报「连不上」，不暴露服务端地址：地址属于内部部署信息，用户也无法据此做任何事。
            throw new RelayApiException(HttpStatusCode.ServiceUnavailable,
                "无法连接中继服务，请检查网络后重试", ex.Message);
        }

        string text;
        using (response)
        {
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
            throw new RelayApiException(response.StatusCode, ParseError(text, response.StatusCode), text);

        try
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<T>(text);
            return value ?? throw new RelayApiException(response.StatusCode, "服务端返回了空响应", text);
        }
        catch (System.Text.Json.JsonException)
        {
            // 多半是域名指错、被反向代理拦掉或网关返回了 HTML 错误页。
            throw new RelayApiException(response.StatusCode,
                "中继服务暂时不可用，请稍后重试", text);
        }
    }

    private static string ParseError(string body, HttpStatusCode status)
    {
        try
        {
            var err = System.Text.Json.JsonSerializer.Deserialize<RelayErrorBody>(body)?.Error;
            if (!string.IsNullOrWhiteSpace(err)) return Describe(err, status);
        }
        catch (System.Text.Json.JsonException)
        {
            // 落到下面的通用文案
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "登录已失效，请重新登录",
            HttpStatusCode.TooManyRequests => "操作过于频繁，请稍后再试",
            HttpStatusCode.BadGateway => "服务端发送邮件失败，请稍后重试",
            _ => $"服务端返回 {(int)status}"
        };
    }

    private static string Describe(string code, HttpStatusCode status) => code switch
    {
        "invalid_email" => "邮箱格式不正确",
        "captcha_wrong" => "人机验证答案错误",
        "captcha_expired" => "人机验证已过期，请刷新",
        "captcha_not_found" => "人机验证无效，请刷新",
        "ip_locked" => "当前网络验证失败次数过多，已临时锁定",
        "too_frequent" => "验证码已发送，请 60 秒后再试",
        "hourly_limit" => "本小时发送次数已达上限",
        "code_expired" => "验证码已过期，请重新获取",
        "code_wrong" => "验证码不正确",
        "too_many_attempts" => "验证码错误次数过多，请重新获取",
        "banned" => "该账号已被封禁，请联系管理员",
        "email_domain_not_allowed" => "该邮箱后缀不允许注册，请换一个邮箱",
        "register_limited" => "当前网络注册过于频繁，请稍后再试",
        "register_closed" => "服务器暂时关闭了注册，请稍后再试",
        "nat_range_out_of_node" => "当前节点端口不在允许范围内，请联系管理员",
        "traffic_exhausted" => "本月流量已用完",
        "port_quota_exhausted" => "端口配额已用完，请先释放已有端口",
        "node_offline" => "该节点已离线，请换一个",
        "no_port_available" => "该节点端口已分配完，请换一个",
        "unauthorized" => "登录已失效，请重新登录",
        _ => status == HttpStatusCode.Unauthorized ? "登录已失效，请重新登录" : $"请求失败（{code}）"
    };
}

public sealed class RelayApiException(HttpStatusCode status, string message, string? detail = null)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Body { get; } = detail;
}
