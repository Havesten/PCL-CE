using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net;
using PCL.Core.Link.EasyTier;

namespace PCL.Core.Link.Relay;

/// <summary>
///     中继联机的云端 API 客户端。
///     所有请求都走启动器统一的 HttpClient，因此自定义代理、节点优选对它同样生效。
/// </summary>
public sealed class RelayApi
{
    /// <summary>默认的云端根地址。与更新源共用 <see cref="CloudEndpoints.ApiRoot" />，路径前缀 /api/v1。</summary>
    private static readonly string DefaultBaseUrl = CloudEndpoints.ApiRoot;

    /// <summary>
    ///     云端根地址。设置环境变量 <c>PCL_RELAY_API</c> 可以指向本地或自建服务，便于联调。
    /// </summary>
    public static string BaseUrl { get; set; } = ResolveBaseUrl();

    private static string ResolveBaseUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PCL_RELAY_API");
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultBaseUrl : fromEnv.TrimEnd('/');
    }

    // 用专用的短超时客户端（12 秒），而不是全局默认客户端的 100 秒：
    // 联机页在切换时就会发几个请求，超时太长会让整页像「卡死」。
    private readonly HttpClient _client;
    private readonly string? _clientVersion;

    public RelayApi(HttpClient? client = null) =>
        _client = client ?? NetworkService.GetClient(NetworkService.Relay);

    internal RelayApi(HttpClient client, string clientVersion) : this(client) => _clientVersion = clientVersion;

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

    /// <summary>编辑姓名时仅允许中文汉字，最多六字；允许空值用于删除输入。</summary>
    public static bool IsAllowedRealNameInput(string value)
    {
        if (value is null) return false;
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var code = rune.Value;
            if (code != 0x3007 && code is not (>= 0x3400 and <= 0x4DBF) &&
                code is not (>= 0x4E00 and <= 0x9FFF) && code is not (>= 0xF900 and <= 0xFAFF) &&
                code is not (>= 0x20000 and <= 0x2FA1F) && code is not (>= 0x30000 and <= 0x323AF))
                return false;
            if (code != 0x3007 && Rune.GetUnicodeCategory(rune) != UnicodeCategory.OtherLetter) return false;
            if (++count > 6) return false;
        }
        return true;
    }

    /// <summary>姓名必须由二至六个中文汉字组成。</summary>
    public static bool IsValidRealName(string value) =>
        IsAllowedRealNameInput(value) && value.EnumerateRunes().Count() >= 2;

    /// <summary>编辑身份证号时只允许数字，X 仅可出现在第十八位。</summary>
    public static bool IsAllowedIdentityInput(string value)
    {
        if (value is null || value.Length > 18) return false;
        for (var i = 0; i < value.Length; i++)
            if ((value[i] is < '0' or > '9') && !(i == 17 && value[i] is 'X' or 'x')) return false;
        return true;
    }

    // 日期上限以中国时区当天及云端下发范围为准，避免客户端时区不同造成当天误判。
    private static DateOnly RealNameToday => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);

    private static bool TryGetRealNameValidationDate(RelayRealNameRules rules, out DateOnly today)
    {
        today = RealNameToday;
        return rules.MinimumAge is >= 0 and <= 150 && (rules.ValidationDate is null ||
            DateOnly.TryParseExact(rules.ValidationDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out today));
    }

    internal static bool TryGetRealNameBirthRange(RelayRealNameRules rules, out DateOnly minimum, out DateOnly maximum)
    {
        minimum = default;
        maximum = default;
        if (rules.NameMinLength != 2 || rules.NameMaxLength != 6 || rules.IdentityNumberLength != 18 ||
            !TryGetRealNameValidationDate(rules, out var today) ||
            !DateOnly.TryParseExact(rules.BirthDateMin, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out minimum))
            return false;
        if (rules.BirthDateMax is null) maximum = today;
        else if (!DateOnly.TryParseExact(rules.BirthDateMax, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out maximum))
            return false;
        return minimum <= maximum && maximum <= today;
    }

    // 与云端 Go AddDate 保持一致：二月二十九日出生者在非闰年的三月一日达到相应周岁。
    private static bool MeetsRealNameMinimumAge(DateOnly birthday, RelayRealNameRules rules)
    {
        if (!TryGetRealNameValidationDate(rules, out var today) || birthday > today) return false;
        try
        {
            var anniversaryYear = birthday.Year + rules.MinimumAge;
            var anniversary = birthday.Month == 2 && birthday.Day == 29 && !DateTime.IsLeapYear(anniversaryYear)
                ? new DateOnly(anniversaryYear, 3, 1)
                : birthday.AddYears(rules.MinimumAge);
            return anniversary <= today;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>仅核对身份证出生日期是否达到云端最低周岁；调用前应先验证证件格式。</summary>
    public static bool IsRealNameMinimumAgeSatisfied(SecureString value, RelayRealNameRules rules)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(rules);
        if (value.Length != 18) return false;
        var chars = new char[value.Length];
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(value);
            Marshal.Copy(pointer, chars, 0, chars.Length);
            return DateOnly.TryParseExact(chars.AsSpan(6, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var birthday) && MeetsRealNameMinimumAge(birthday, rules);
        }
        finally
        {
            Array.Clear(chars);
            if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    /// <summary>验证十八位身份证号、实际出生日期、云端日期范围与校验位。</summary>
    public static bool IsValidIdentityNumber(SecureString value, RelayRealNameRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 18) return false;
        rules ??= new RelayRealNameRules();
        if (!TryGetRealNameBirthRange(rules, out var minimum, out var maximum)) return false;

        var chars = new char[value.Length];
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(value);
            Marshal.Copy(pointer, chars, 0, chars.Length);
            var weightedSum = 0;
            ReadOnlySpan<int> weights = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
            const string checkCharacters = "10X98765432";
            for (var i = 0; i < 17; i++)
            {
                if (chars[i] is < '0' or > '9') return false;
                weightedSum += (chars[i] - '0') * weights[i];
            }
            if (chars[0] == '0' || chars[14] == '0' && chars[15] == '0' && chars[16] == '0') return false;
            if (!rules.AllowChecksumX && char.ToUpperInvariant(chars[17]) == 'X') return false;
            if (char.ToUpperInvariant(chars[17]) != checkCharacters[weightedSum % 11]) return false;

            var year = (chars[6] - '0') * 1000 + (chars[7] - '0') * 100 + (chars[8] - '0') * 10 + chars[9] - '0';
            var month = (chars[10] - '0') * 10 + chars[11] - '0';
            var day = (chars[12] - '0') * 10 + chars[13] - '0';
            DateOnly birthday;
            try { birthday = new DateOnly(year, month, day); }
            catch (ArgumentOutOfRangeException) { return false; }
            return birthday >= minimum && birthday <= maximum && MeetsRealNameMinimumAge(birthday, rules);
        }
        finally
        {
            Array.Clear(chars);
            if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    /// <summary>获取当前条款及阅读凭据；不提交任何实名资料。</summary>
    public Task<RelayRealNameTermsRead> OpenRealNameTermsAsync(string termsVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(termsVersion);
        return SendAsync<RelayRealNameTermsRead>(HttpMethod.Post, "/api/v1/relay/real-name/terms/read",
            new { terms_version = termsVersion }, ct);
    }

    /// <summary>提交使用 AES-GCM + 云端 RSA 公钥封装的实名信息；要求阅读凭据及两项独立授权。</summary>
    public Task<RelayRealNameState> StartRealNameAsync(string publicKeyBase64, string name,
        SecureString identityNumber, string termsVersion, string termsReadToken,
        bool sensitiveDataConsented, bool alipayDataConsented, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identityNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(termsVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(termsReadToken);
        if (!sensitiveDataConsented) throw new ArgumentException("请单独确认实名敏感个人信息处理授权。", nameof(sensitiveDataConsented));
        if (!alipayDataConsented) throw new ArgumentException(
            "请单独确认向支付宝认证服务提供方提供实名资料的授权。", nameof(alipayDataConsented));
        if (!IsValidRealName(name)) throw new ArgumentException("姓名须为二至六个中文汉字。", nameof(name));
        if (identityNumber.Length != 18) throw new ArgumentException("身份证号码须为十八位。", nameof(identityNumber));
        var envelope = EncryptRealNamePayload(publicKeyBase64, name, identityNumber);
        return SendAsync<RelayRealNameState>(HttpMethod.Post, "/api/v1/relay/real-name/start",
            new
            {
                encrypted_key = envelope.EncryptedKey,
                nonce = envelope.Nonce,
                ciphertext = envelope.Ciphertext,
                consented = true,
                terms_version = termsVersion,
                terms_read_token = termsReadToken,
                terms_read_confirmed = true,
                sensitive_data_consented = sensitiveDataConsented,
                alipay_data_consented = alipayDataConsented
            }, ct);
    }

    /// <summary>查询扫码认证进度。返回数据不包含身份证明文。</summary>
    public async Task<RelayRealNameState> GetRealNameStatusAsync(CancellationToken ct = default) =>
        await SendAsync<RelayRealNameState>(HttpMethod.Post, "/api/v1/relay/real-name/status", new { }, ct)
            .ConfigureAwait(false);

    private static RealNameEnvelope EncryptRealNamePayload(string publicKeyBase64, string name, SecureString identityNumber)
    {
        ArgumentNullException.ThrowIfNull(identityNumber);
        var identityChars = new char[identityNumber.Length];
        var identityPointer = IntPtr.Zero;
        byte[]? key = null;
        byte[]? nonce = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? combinedCiphertext = null;
        byte[]? wrappedKey = null;
        byte[]? publicKey = null;
        ArrayBufferWriter<byte>? jsonBuffer = null;
        try
        {
            identityPointer = Marshal.SecureStringToGlobalAllocUnicode(identityNumber);
            Marshal.Copy(identityPointer, identityChars, 0, identityChars.Length);
            jsonBuffer = new ArrayBufferWriter<byte>(512);
            using (var writer = new Utf8JsonWriter(jsonBuffer, new JsonWriterOptions { SkipValidation = false }))
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("identity_number", identityChars.AsSpan());
                writer.WriteEndObject();
            }

            // AES-GCM carries the variable-length JSON payload; RSA only wraps the fixed-size AES key.
            key = RandomNumberGenerator.GetBytes(32);
            nonce = RandomNumberGenerator.GetBytes(12);
            ciphertext = new byte[jsonBuffer.WrittenCount];
            tag = new byte[16];
            using (var aes = new AesGcm(key, tag.Length))
                aes.Encrypt(nonce, jsonBuffer.WrittenSpan, ciphertext, tag);

            using var rsa = RSA.Create();
            publicKey = Convert.FromBase64String(publicKeyBase64);
            rsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length) throw new CryptographicException("实名公钥格式无效");
            wrappedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);

            // The ciphertext field is ciphertext bytes followed by the 16-byte GCM tag.
            combinedCiphertext = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(combinedCiphertext, 0);
            tag.CopyTo(combinedCiphertext, ciphertext.Length);
            return new RealNameEnvelope(
                Convert.ToBase64String(wrappedKey),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(combinedCiphertext));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("云端实名公钥格式无效，请联系管理员", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("无法加密实名信息，请更新客户端或联系管理员", ex);
        }
        finally
        {
            Array.Clear(identityChars);
            if (identityPointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(identityPointer);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (tag is not null) CryptographicOperations.ZeroMemory(tag);
            if (combinedCiphertext is not null) CryptographicOperations.ZeroMemory(combinedCiphertext);
            if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            if (wrappedKey is not null) CryptographicOperations.ZeroMemory(wrappedKey);
            if (jsonBuffer is not null && MemoryMarshal.TryGetArray(jsonBuffer.WrittenMemory, out var jsonSegment))
                CryptographicOperations.ZeroMemory(jsonSegment.AsSpan());
        }
    }

    private sealed record RealNameEnvelope(string EncryptedKey, string Nonce, string Ciphertext);

    /// <summary>
    ///     发送绑定手机号的短信验证码（需已登录 + 人机验证）。
    ///     返回开发模式下服务端回显的验证码（线上为 null）。
    /// </summary>
    public async Task<string?> SendSmsCodeAsync(string phone, string captchaId, string captchaAnswer,
        CancellationToken ct = default)
    {
        var res = await SendAsync<RelaySendCodeResult>(HttpMethod.Post, "/api/v1/auth/sms/send-code", new
        {
            phone,
            captcha_id = captchaId,
            captcha_answer = captchaAnswer
        }, ct).ConfigureAwait(false);
        return res.DevCode;
    }

    /// <summary>校验短信验证码并把手机号绑定到当前账号。</summary>
    public async Task<RelayPhoneState?> BindPhoneAsync(string phone, string code, CancellationToken ct = default)
    {
        var res = await SendAsync<RelaySmsBindResult>(HttpMethod.Post, "/api/v1/auth/sms/bind", new
        {
            phone,
            code
        }, ct).ConfigureAwait(false);
        return res.Phone;
    }

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

    public async Task<RelayAllocationResult> CreateRoomAsync(string nodeId, string name, string gameVersion,
        CancellationToken ct = default) =>
        await SendAsync<RelayAllocationResult>(HttpMethod.Post, "/api/v1/relay/allocations", new
        {
            node_id = nodeId,
            label = name,
            room = true,
            game_version = gameVersion
        }, ct).ConfigureAwait(false);

    public async Task SetRoomReadyAsync(string allocationId, bool ready, CancellationToken ct = default)
    {
        var result = await SendAsync<RelayRoomReadyResult>(HttpMethod.Post, "/api/v1/relay/rooms/ready", new
        {
            id = allocationId,
            ready
        }, ct).ConfigureAwait(false);
        if (!result.Ok) throw new RelayApiException(HttpStatusCode.ServiceUnavailable, "云端未确认房间状态，请稍后重试");
    }

    public async Task<RelayRoom> JoinRoomAsync(string code, CancellationToken ct = default)
    {
        if (!RelayRoomCode.TryNormalize(code, out var normalized))
            throw new ArgumentException("房间码格式不正确，请粘贴完整的五组房间码", nameof(code));
        return (await SendAsync<RelayRoomResult>(HttpMethod.Post, "/api/v1/relay/rooms/join",
            new { code = normalized }, ct).ConfigureAwait(false)).Room;
    }

    public async Task<IReadOnlyList<P2pRendezvousNode>> GetP2pNodesAsync(CancellationToken ct = default) =>
        (await SendAsync<P2pNodeList>(HttpMethod.Get, "/api/v1/p2p/nodes", null, ct).ConfigureAwait(false)).Nodes ?? [];

    public async Task<P2pCloudRoom> CreateP2pRoomAsync(string nodeId, string name, string gameVersion, int gamePort,
        CancellationToken ct = default) =>
        (await SendAsync<P2pRoomResult>(HttpMethod.Post, "/api/v1/p2p/rooms",
            new { node_id = nodeId, label = name, game_version = gameVersion, game_port = gamePort }, ct).ConfigureAwait(false)).Room;

    public async Task<P2pCloudRoom> JoinP2pRoomAsync(string code, CancellationToken ct = default)
    {
        if (!RelayRoomCode.TryNormalize(code, out var normalized))
            throw new ArgumentException("请填写完整的五组房间码", nameof(code));
        return (await SendAsync<P2pRoomResult>(HttpMethod.Post, "/api/v1/p2p/rooms/join",
            new { code = normalized }, ct).ConfigureAwait(false)).Room;
    }

    public async Task SetP2pRoomReadyAsync(string id, bool ready, CancellationToken ct = default)
    {
        var result = await SendAsync<RelayRoomReadyResult>(HttpMethod.Post, "/api/v1/p2p/rooms/ready",
            new { id, ready }, ct).ConfigureAwait(false);
        if (!result.Ok) throw new RelayApiException(HttpStatusCode.ServiceUnavailable, "云端未确认房间状态");
    }

    public async Task ReleaseP2pRoomAsync(string id, CancellationToken ct = default) =>
        _ = await SendAsync<RelayRoomReadyResult>(HttpMethod.Post, "/api/v1/p2p/rooms/release", new { id }, ct).ConfigureAwait(false);

    public async Task<P2pRoomStatus> GetP2pRoomStatusAsync(string id, string? phase = null, string? failureCode = null,
        int retryCount = 0, CancellationToken ct = default)
    {
        P2pRoomStatus? status;
        try
        {
            status = (await SendAsync<P2pRoomStatusResult>(HttpMethod.Post, "/api/v1/p2p/rooms/status",
                new { id, phase, failure_code = failureCode ?? "none", retry_count = Math.Clamp(retryCount, 0, 99) }, ct).ConfigureAwait(false)).Status;
        }
        catch (RelayApiException ex) when (ex.Status == HttpStatusCode.NotFound &&
            ex.Body?.Contains("room_not_found", StringComparison.Ordinal) != true)
        { throw new RelayApiException(ex.Status, "云端尚未支持房间状态查询，请联系管理员更新云端"); }
        if (status is null || string.IsNullOrWhiteSpace(status.Id) || string.IsNullOrWhiteSpace(status.Generation) ||
            status.ExpiresAt == default || status.State is not ("active" or "unready"))
            throw new InvalidOperationException("云端房间状态暂时无效，请稍后重试");
        return status;
    }

    // ---------------------------------------------------------------- 传输层

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        // 联机功能版本门槛：带上启动器版本号，服务端要求版本不一致时会拒绝分配端口。
        request.Headers.TryAddWithoutValidation("X-Client-Version", _clientVersion ?? Basics.VersionName);
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
            var err = System.Text.Json.JsonSerializer.Deserialize<RelayErrorBody>(body);
            if (!string.IsNullOrWhiteSpace(err?.Error))
            {
                // 短信失败的具体原因只有服务端知道（阿里云返回码翻好的中文），有就原样带给用户，
                // 否则只会看到「短信发送失败」这种帮不上忙的提示。
                if (err.Error == "sms_failed" && !string.IsNullOrWhiteSpace(err.Detail))
                    return "短信发送失败：" + err.Detail;
                if (err.Error == "too_frequent" && err.RetryIn is int wait && wait > 0)
                    return $"验证码已发送，请 {wait} 秒后再试";
                return Describe(err.Error, status);
            }
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
        "invalid_phone" => "手机号格式不正确：需 11 位、1 开头，且不能有 7 位及以上连号",
        "phone_taken" => "该手机号已绑定其他账号",
        "phone_required" => "请先绑定手机号",
        "phone_hourly_limit" => "该号码本小时发送次数已达上限",
        "sms_closed" => "服务器暂时关闭了短信发送",
        "sms_not_configured" => "服务端未配置短信服务，请联系管理员",
        "sms_failed" => "短信发送失败，请稍后重试",
        "nat_range_out_of_node" => "当前节点端口不在允许范围内，请联系管理员",
        "traffic_exhausted" => "本月流量已用完",
        "port_quota_exhausted" => "端口配额已用完，请先释放已有端口",
        "node_offline" => "该节点已离线，请换一个",
        "node_upgrade_required" => "该节点需要升级至 3.2.1 才能创建房间码，请选择其他节点或联系管理员",
        "no_port_available" => "该节点端口已分配完，请换一个",
        "room_code_invalid" => "房间码格式不正确，请粘贴完整的五组房间码",
        "room_code_unavailable" => "房间码暂时生成失败，请稍后重试",
        "room_not_found" => "房间码无效或房间已关闭",
        "room_not_ready" => "房间暂时不可用，请确认房主的游戏仍在运行后重试",
        "room_rate_limited" => "房间码查询过于频繁，请一分钟后再试",
        "p2p_disabled" => "P2P 联机已由管理员关闭",
        "relay_disabled" => "中继联机已由管理员关闭",
        "p2p_node_unavailable" => "会合节点暂不可用，请选择其他节点",
        "p2p_room_exists" => "已有 P2P 房间，请先关闭后再创建",
        "room_full" => "房间成员数已达上限，请联系房主",
        "version_too_low" => "版本过低，需要更新",
        // 服务端按来源 IP 判定不在中国大陆时返回；文案由服务端下发，这里只是兜底
        "region_blocked" => "当前可能位于非中国大陆地区，暂时无法使用此功能",
        "unauthorized" => "登录已失效，请重新登录",
        "real_name_required" => "使用中继联机服务前，请先完成实名认证。",
        "real_name_disabled" => "实名认证服务暂未启用，请联系服务提供方。",
        "real_name_provider_unconfigured" => "实名认证服务尚未配置完成，请联系服务提供方。",
        "real_name_policy_unavailable" => "实名认证规则暂不可用，请稍后重试或联系服务管理员。",
        "real_name_locked_or_pending" => "认证已完成或正在处理中，暂不接受重复提交。",
        "real_name_consent_required" => "请先阅读中继服务条款并明确同意实名资料授权。",
        "relay_terms_changed" => "中继服务条款已更新，请重新阅读并确认同意后提交。",
        "relay_terms_read_required" => "请重新打开并完整阅读中继服务条款，确认后再提交实名认证。",
        "relay_terms_read_too_short" => "条款阅读时间尚未达到要求，请在倒计时结束后确认。",
        "relay_terms_read_limited" => "当前条款阅读请求较多，请稍后重试。",
        "real_name_sensitive_consent_required" => "请单独确认实名敏感个人信息处理授权。",
        "real_name_alipay_consent_required" => "请单独确认向支付宝认证服务提供方提供实名资料的授权。",
        "invalid_real_name" => "姓名须为二至六个中文汉字。",
        "invalid_identity_number" => "身份证号码格式不正确，请核对出生日期、顺序码及末位校验码。",
        "identity_birth_date_out_of_range" => "身份证出生日期不在服务允许范围内，请核对资料或联系服务提供方。",
        "identity_age_below_minimum" => "根据当前服务规则，您尚未达到实名认证最低年龄要求。",
        "real_name_interval_limited" => "实名申请提交过于频繁，请稍后重试。",
        "real_name_daily_limited" => "本账号今日实名申请次数已达上限，请于次日重试。",
        "real_name_ip_limited" => "当前网络实名申请次数已达上限，请稍后重试。",
        "real_name_provider_failed" => "实名认证服务暂时不可用，请稍后重试。",
        "real_name_state_changed" => "认证状态已变更，请刷新后重试。",
        "real_name_state_persist_failed" => "认证状态暂时无法保存，请稍后重试。",
        _ => status == HttpStatusCode.Unauthorized ? "登录已失效，请重新登录" : $"请求失败（{code}）"
    };
}

public sealed class RelayApiException(HttpStatusCode status, string message, string? detail = null)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Body { get; } = detail;
}
