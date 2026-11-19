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

/// <summary>客户端账号视图；Phone 与 RealNameIdMasked 只承载云端返回的脱敏资料。</summary>
public sealed record RelayUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("banned")] bool Banned = false,
    [property: JsonPropertyName("phone")] string? Phone = null,
    [property: JsonPropertyName("phone_bound")] bool PhoneBound = false,
    [property: JsonPropertyName("phone_bound_at")] DateTimeOffset? PhoneBoundAt = null,
    [property: JsonPropertyName("real_name_enabled")] bool? RealNameEnabled = null,
    [property: JsonPropertyName("real_name_status")] string? RealNameStatus = null,
    [property: JsonPropertyName("real_name_verified")] bool RealNameVerified = false,
    [property: JsonPropertyName("real_name_id_masked")] string? RealNameIdMasked = null);

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
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("room_codes")] bool RoomCodes = false)
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
    [property: JsonPropertyName("bytes_out")] long BytesOut,
    [property: JsonPropertyName("room_code")] string? RoomCode = null,
    [property: JsonPropertyName("game_version")] string? GameVersion = null,
    [property: JsonPropertyName("room_ready")] bool RoomReady = false);

public sealed record RelayRoom(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("game_version")] string GameVersion,
    [property: JsonPropertyName("node_name")] string NodeName,
    [property: JsonPropertyName("server_host")] string ServerHost,
    [property: JsonPropertyName("server_port")] int ServerPort,
    [property: JsonPropertyName("join_token")] string JoinToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

internal sealed record RelayRoomResult([property: JsonPropertyName("room")] RelayRoom Room);
internal sealed record RelayRoomReadyResult([property: JsonPropertyName("ok")] bool Ok);

/// <summary>
///     自研隧道（PTP v1）连接参数。服务端分配时下发，客户端据此建立隧道。
/// </summary>
public sealed record RelayTunnel(
    [property: JsonPropertyName("server_addr")] string ServerAddr,
    [property: JsonPropertyName("server_port")] int ServerPort,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("remote_port")] int RemotePort,
    [property: JsonPropertyName("sync_interval_seconds")] int SyncIntervalSeconds = 5);

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
    [property: JsonPropertyName("required_version")] string? RequiredVersion = null,
    // 地区限制：region_check 为 true 表示服务端启用了该限制；
    // region_allowed 为 false 时客户端应直接禁用联机功能。
    // 两个字段都缺省为「不限制」，老服务端不会把客户端拦下来。
    [property: JsonPropertyName("region_check")] bool RegionCheck = false,
    [property: JsonPropertyName("region_allowed")] bool RegionAllowed = true,
    [property: JsonPropertyName("region")] string? Region = null,
    [property: JsonPropertyName("room_codes")] bool RoomCodes = false,
    [property: JsonPropertyName("room_code_prefix")] string? RoomCodePrefix = null,
    [property: JsonPropertyName("p2p_enabled")] bool P2pEnabled = true,
    [property: JsonPropertyName("relay_enabled")] bool RelayEnabled = true,
    [property: JsonPropertyName("p2p_room_codes")] bool P2pRoomCodes = false,
    [property: JsonPropertyName("real_name_enabled")] bool RealNameEnabled = false,
    [property: JsonPropertyName("real_name_public_key")] string? RealNamePublicKey = null,
    [property: JsonPropertyName("real_name_rules")] RelayRealNameRules? RealNameRules = null,
    [property: JsonPropertyName("relay_terms")] RelayServiceTerms? RelayTerms = null);

/// <summary>云端下发的实名格式、出生日期及最低周岁规则；资料提交仍由云端再次验证。</summary>
public sealed record RelayRealNameRules(
    [property: JsonPropertyName("name_min_length")] int NameMinLength = 2,
    [property: JsonPropertyName("name_max_length")] int NameMaxLength = 6,
    [property: JsonPropertyName("identity_number_length")] int IdentityNumberLength = 18,
    [property: JsonPropertyName("allow_checksum_x")] bool AllowChecksumX = true,
    [property: JsonPropertyName("birth_date_min")] string BirthDateMin = "1900-01-01",
    [property: JsonPropertyName("birth_date_max")] string? BirthDateMax = null,
    [property: JsonPropertyName("minimum_age")] int MinimumAge = 0,
    [property: JsonPropertyName("validation_date")] string? ValidationDate = null)
{
    [JsonIgnore]
    public bool IsValidConfiguration => BirthDateMax is not null && ValidationDate is not null &&
        RelayApi.TryGetRealNameBirthRange(this, out _, out _);
}

/// <summary>中继服务条款原文与版本，由云端后台维护。</summary>
public sealed record RelayServiceTerms(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("content")] string Content);

/// <summary>绑定当前用户及条款版本的阅读凭据；实名提交时由云端复核最短阅读时长。</summary>
public sealed record RelayRealNameTermsRead(
    [property: JsonPropertyName("terms")] RelayServiceTerms Terms,
    [property: JsonPropertyName("read_token")] string ReadToken,
    [property: JsonPropertyName("minimum_read_seconds")] int MinimumReadSeconds,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>实名提交、扫码进度查询的服务端状态；不会包含身份证明文。</summary>
public sealed record RelayRealNameState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("qr_png")] string? QrPng,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("masked_id")] string? MaskedId);

internal sealed record RelayErrorBody(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("retry_in")] int? RetryIn = null);

internal sealed record RelaySendCodeResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("ttl")] int Ttl,
    [property: JsonPropertyName("dev_code")] string? DevCode);

/// <summary>绑定手机号接口的返回，phone 字段为绑定后的脱敏号码状态。</summary>
internal sealed record RelaySmsBindResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("phone")] RelayPhoneState? Phone);

/// <summary>手机绑定状态；Phone 只包含脱敏号码，不提供完整手机号。</summary>
public sealed record RelayPhoneState(
    [property: JsonPropertyName("phone")] string Phone,
    [property: JsonPropertyName("bound")] bool Bound,
    [property: JsonPropertyName("bound_at")] DateTimeOffset? BoundAt);
