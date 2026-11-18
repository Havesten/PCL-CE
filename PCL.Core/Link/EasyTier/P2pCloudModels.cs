using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCL.Core.Link.EasyTier;

public sealed record P2pRendezvousNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("uri")] string Uri)
{
    public override string ToString() => Name;
}

public sealed record P2pCloudRoom(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("game_version")] string GameVersion,
    [property: JsonPropertyName("game_port")] int GamePort,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("node_name")] string NodeName,
    [property: JsonPropertyName("rendezvous_uri")] string RendezvousUri,
    [property: JsonPropertyName("network_name")] string NetworkName,
    [property: JsonPropertyName("network_secret")] string NetworkSecret,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

internal sealed record P2pNodeList([property: JsonPropertyName("nodes")] IReadOnlyList<P2pRendezvousNode>? Nodes);
internal sealed record P2pRoomResult([property: JsonPropertyName("room")] P2pCloudRoom Room);
