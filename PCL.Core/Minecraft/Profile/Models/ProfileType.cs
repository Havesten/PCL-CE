using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Profile.Models;

[JsonConverter(typeof(ProfileTypeConverter))]
public enum ProfileType
{
    Microsoft
}

/// <summary>
/// 兼容历史档案数据的类型转换器。旧版本曾持久化 Offline / Authlib / YggdrasilConnect 等类型，
/// 这些登录方式已随第三方登录支持一并移除，读取时统一回落为 <see cref="ProfileType.Microsoft" />，
/// 对应档案将在下次启动时要求重新进行 Microsoft 登录。
/// </summary>
public sealed class ProfileTypeConverter : JsonConverter<ProfileType>
{
    public override ProfileType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 任意历史取值（Offline / Authlib / YggdrasilConnect / 数字）均回落为 Microsoft。
        return ProfileType.Microsoft;
    }

    public override void Write(Utf8JsonWriter writer, ProfileType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(nameof(ProfileType.Microsoft));
    }
}
