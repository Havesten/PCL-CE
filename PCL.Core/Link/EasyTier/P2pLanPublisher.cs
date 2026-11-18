using System;
using PCL.Core.Link.Relay;

namespace PCL.Core.Link.EasyTier;

public sealed class P2pLanPublisher : IDisposable
{
    private readonly RelayLanAnnouncer _announcer;
    private readonly int _port;
    public bool Enabled { get => _announcer.Enabled; set => _announcer.Enabled = value; }
    public P2pLanPublisher(string name, int port)
    {
        _port = port;
        RelayGameDiscovery.RegisterInternalPort(port);
        _announcer = new RelayLanAnnouncer("P2P 房间 · " + name, port) { Enabled = false };
        _announcer.Start();
    }
    public void Dispose() { _announcer.Dispose(); RelayGameDiscovery.UnregisterInternalPort(_port); }
}
