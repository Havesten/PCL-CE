using System;

namespace PCL.Core.Link.EasyTier;

public enum P2pConnectionPhase { WaitingHost, Connecting, Validating, Connected, Hosting, Reconnecting, Failed }

public sealed record P2pRecoveryDecision(P2pConnectionPhase Phase, bool Restart, string? FailureCode, int RetryCount);

/// <summary>使用单调时间限制首次连接与恢复过程；正常空房间不触发重连。</summary>
public sealed class P2pConnectionRecovery
{
    private readonly bool _hosting;
    private readonly TimeSpan _initialLimit;
    private readonly TimeSpan _recoveryLimit;
    private TimeSpan? _lostAt;
    private TimeSpan? _readySince;
    private bool _wasReady;
    private int _retries;
    private TimeSpan? _lastRestart;

    public P2pConnectionRecovery(bool hosting, TimeSpan? initialLimit = null, TimeSpan? recoveryLimit = null)
    {
        _hosting = hosting;
        _initialLimit = initialLimit ?? TimeSpan.FromSeconds(60);
        _recoveryLimit = recoveryLimit ?? TimeSpan.FromSeconds(120);
    }

    public P2pRecoveryDecision Observe(TimeSpan elapsed, bool processHealthy, bool peerSeen, bool direct,
        bool gameReady, bool networkChanged = false, bool canRestart = true)
    {
        var ready = processHealthy && (_hosting || direct && gameReady);
        if (ready && !networkChanged)
        {
            _wasReady = true;
            _readySince ??= elapsed;
            // 重启引发的网卡事件可能延后到达；稳定一段时间后才开始新的恢复预算。
            if (!_lostAt.HasValue || elapsed - _readySince >= TimeSpan.FromSeconds(15))
            { _lostAt = null; _retries = 0; _lastRestart = null; }
            return new(_hosting ? P2pConnectionPhase.Hosting : P2pConnectionPhase.Connected, false, null, _retries);
        }
        _readySince = null;
        if (_wasReady || _hosting) _lostAt ??= elapsed;
        var age = _lostAt is { } lost ? elapsed - lost : elapsed;
        if (age >= (_lostAt.HasValue ? _recoveryLimit : _initialLimit))
            return new(P2pConnectionPhase.Failed, false,
                !processHealthy ? "process_exited" : !direct ? "connect_timeout" : "game_unavailable", _retries);

        var phase = _lostAt.HasValue ? P2pConnectionPhase.Reconnecting :
            !peerSeen ? P2pConnectionPhase.WaitingHost : !direct ? P2pConnectionPhase.Connecting : P2pConnectionPhase.Validating;
        // 原生连接先获得恢复机会；重启会断开原 TCP 会话，因此不在健康路径上周期重启。
        var due = _retries switch { 0 => TimeSpan.FromSeconds(10), 1 => TimeSpan.FromSeconds(25), _ => TimeSpan.FromSeconds(45) };
        var restart = canRestart && _retries < 3 && (!_lastRestart.HasValue || elapsed - _lastRestart.Value >= TimeSpan.FromSeconds(10)) &&
            (networkChanged || age >= due);
        if (restart) { _retries++; _lastRestart = elapsed; }
        return new(phase, restart, null, _retries);
    }
}
