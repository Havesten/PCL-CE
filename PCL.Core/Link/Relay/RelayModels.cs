using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCL.Core.Link.Relay;

/// <summary>
///     人机验证挑战。题目由服务端下发，答案在本地算出即可，不涉及任何第三方 SDK。
/// </summary>
public sealed record RelayCaptcha(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("expires")] int ExpiresIn,
    [property: JsonPropertyName("turnstile")] RelayTurnstile Turnstile);

public sealed record RelayTurnstile(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("site_key")] string? SiteKey);

/// <summary>
///     配额视图。所有字段都是服务端算好的展示值，客户端改本地文件不会影响实际限制。
/// </summary>
public sealed record RelayQuota(
    [property: JsonPropertyName("monthly_traffic_bytes")] long MonthlyTrafficBytes,
    [property: JsonPropertyName("used_traffic_bytes")] long UsedTrafficBytes,
    [property: JsonPropertyName("remaining_bytes")] long RemainingBytes,
    [property: JsonPropertyName("rate_mbps")] int RateMbps,
    [property: JsonPropertyName("port_quota")] int PortQuota,
    [property: JsonPropertyName("port_used")] int PortUsed,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("group_name")] string? GroupName = null,
    [property: JsonPropertyName("custom_quota")] bool CustomQuota = false)
{
    public double UsageRatio =>
        MonthlyTrafficBytes <= 0 ? 0 : Math.Clamp((double)UsedTrafficBytes / MonthlyTrafficBytes, 0, 1);
}

public sealed record RelayUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("banned")] bool Banned = false,
    [property: JsonPropertyName("phone")] string? Phone = null,
    [property: JsonPropertyName("phone_bound")] bool PhoneBound = false,
    [property: JsonPropertyName("phone_bound_at")] DateTimeOffset? PhoneBoundAt = null);

public sealed record RelayLoginResult(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("is_new")] bool IsNew,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("user")] RelayUser User,
    [property: JsonPropertyName("quota")] RelayQuota Quota,
    [property: JsonPropertyName("force_bind_phone")] bool ForceBindPhone = false,
    [property: JsonPropertyName("phone_required")] bool PhoneRequired = false);

public sealed record RelayNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("load")] int Load,
    [property: JsonPropertyName("capacity")] int Capacity)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Region)
            ? Name
            : $"{Name}（{Region}）· {Load}/{Capacity}";
}

public sealed record RelayAllocation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("node_name")] string NodeName,
    [property: JsonPropertyName("public_host")] string PublicHost,
    [property: JsonPropertyName("public_port")] int PublicPort,
    [property: JsonPropertyName("internal_port")] int InternalPort,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("bytes_in")] long BytesIn,
    [property: JsonPropertyName("bytes_out")] long BytesOut)
{
    /// <summary>
    ///     分享给朋友的地址。中继模式下房主不需要公网 IP。
    /// </summary>
    public string GameAddress => $"{PublicHost}:{PublicPort}";
}

/// <summary>
///     自研隧道（PTP v1）连接参数。服务端分配时下发，客户端据此建立隧道。
/// </summary>
public sealed record RelayTunnel(
    [property: JsonPropertyName("server_addr")] string ServerAddr,
    [property: JsonPropertyName("server_port")] int ServerPort,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("remote_port")] int RemotePort);

public sealed record RelayAllocationResult(
    [property: JsonPropertyName("allocation")] RelayAllocation Allocation,
    [property: JsonPropertyName("tunnel")] RelayTunnel Tunnel,
    [property: JsonPropertyName("quota")] RelayQuota Quota);

public sealed record RelayMe(
    [property: JsonPropertyName("user")] RelayUser User,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("quota")] RelayQuota Quota,
    [property: JsonPropertyName("allocations")] IReadOnlyList<RelayAllocation> Allocations,
    [property: JsonPropertyName("force_bind_phone")] bool ForceBindPhone = false,
    [property: JsonPropertyName("phone_required")] bool PhoneRequired = false)
{
    /// <summary>服务端下发的账号状态，banned 时客户端不允许再分配端口。</summary>
    public bool IsBanned => string.Equals(Status, "banned", StringComparison.OrdinalIgnoreCase);
}

public sealed record RelayNodeList(
    [property: JsonPropertyName("nodes")] IReadOnlyList<RelayNode> Nodes);

public sealed record RelayStatus(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("captcha")] string Captcha,
    [property: JsonPropertyName("turnstile")] bool Turnstile,
    [property: JsonPropertyName("node_count")] int NodeCount,
    [property: JsonPropertyName("dev_mode")] bool DevMode,
    [property: JsonPropertyName("required_version")] string? RequiredVersion = null);

internal sealed record RelayErrorBody(
    [property: JsonPropertyName("error")] string? Error);

internal sealed record RelaySendCodeResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("ttl")] int Ttl,
    [property: JsonPropertyName("dev_code")] string? DevCode);

/// <summary>绑定手机号接口的返回，phone 字段为绑定后的号码状态。</summary>
internal sealed record RelaySmsBindResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("phone")] RelayPhoneState? Phone);

public sealed record RelayPhoneState(
    [property: JsonPropertyName("phone")] string Phone,
    [property: JsonPropertyName("bound")] bool Bound,
    [property: JsonPropertyName("bound_at")] DateTimeOffset? BoundAt);
