using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.App;

namespace PCL.Core.Link.Relay;

/// <summary>
///     本地登录态。只存令牌与邮箱，令牌是 32 字节随机串的服务端哈希对应值，
///     本身不含任何账号信息；丢失后重新走一次邮箱验证码即可。
/// </summary>
public sealed class RelaySession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? Token { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonIgnore] public bool IsValid => !string.IsNullOrWhiteSpace(Token) && ExpiresAt > DateTimeOffset.Now;

    private static string FilePath => Path.Combine(Paths.SharedData, "relay-session.json");

    public static RelaySession Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new RelaySession();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<RelaySession>(json, JsonOptions) ?? new RelaySession();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new RelaySession();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.SharedData);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不下不影响本次使用，下次重新登录即可
        }
    }

    public void Clear()
    {
        Token = null;
        Email = null;
        ExpiresAt = default;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略
        }
    }

    /// <summary>
    ///     构造一个已挂上本地令牌的 API 客户端。
    /// </summary>
    public static RelayApi CreateApi(RelaySession session)
    {
        return new RelayApi { Token = session.IsValid ? session.Token : null };
    }
}
