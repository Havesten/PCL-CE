using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Profile.Authentication.Utils.Models;

public record MojangProfile
{
    [JsonPropertyName("id")]
    public required string Uuid { get; set; }
    [JsonPropertyName("name")]
    public required string Name { get; set; }
    [JsonPropertyName("properties")]
    public List<PlayerProperty>? Properties { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>
/// Minecraft profile property (e.g. textures) returned by the Mojang profile endpoint.
/// </summary>
public record PlayerProperty
{
    /// <summary>
    /// 属性名称
    /// </summary>
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>
    /// 属性值
    /// </summary>
    [JsonPropertyName("value")] public required string Value { get; init; }
    /// <summary>
    /// 数字签名
    /// </summary>
    [JsonPropertyName("signature")] public string? Signature { get; init; }
}
