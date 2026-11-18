using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Relay;

namespace PCL.Core.Test;

[TestClass]
public class P2pCloudRoomTest
{
    private static readonly P2pCloudRoom Room = new("room", "P2PCE-ABCDE-FGHJK-LMNPQ-RSTUV", "World", "1.21.1", 25565,
        "node", "Node", "tcp://127.0.0.1:11010", new string('a', 32), new string('b', 64), DateTimeOffset.UtcNow.AddHours(1));
    private static P2pRoomStatus Status(bool ready = true, bool available = true) =>
        new(Room.Id, Room.Id, ready, ready && available ? "active" : "unready", Room.ExpiresAt, available);

    [TestMethod]
    public async Task ApiUsesIndependentRoomEndpointAndNormalizesCode()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/api/v1/p2p/rooms/join", StringComparison.Ordinal));
            Assert.AreEqual("3.2.1", string.Join("", request.Headers.GetValues("X-Client-Version")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.AreEqual(Room.Code, body.RootElement.GetProperty("code").GetString());
            return Json(new { room = Room });
        }));
        var api = new RelayApi(http, "3.2.1");
        var room = await api.JoinP2pRoomAsync("p2pce abcde fghjk lmnpq rstuv");
        Assert.AreEqual(Room, room);
    }

    [TestMethod]
    public async Task HostHeartbeatWorksWithoutAnyPageAndReleaseIsOwnerOnly()
    {
        var states = new ConcurrentQueue<bool>();
        var released = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.AreEqual(Room.Id, body.RootElement.GetProperty("id").GetString());
            if (request.RequestUri!.AbsolutePath.EndsWith("/ready", StringComparison.Ordinal))
                states.Enqueue(body.RootElement.GetProperty("ready").GetBoolean());
            else if (request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal)) Interlocked.Increment(ref released);
            else return Json(new { status = Status() });
            return Json(new { ok = true });
        }));
        var lease = new P2pRoomLease(new RelayApi(http, "3.2.1"), Room, true, _ => Task.FromResult(true), TimeSpan.FromMilliseconds(25));
        await lease.StartAsync();
        await Task.Delay(100);
        await lease.DisposeAsync();
        Assert.IsTrue(states.Count >= 2);
        Assert.AreEqual(1, released);
        await lease.DisposeAsync();
        Assert.AreEqual(1, released);
    }

    [TestMethod]
    public async Task GuestDoesNotReleaseOwnersRoomAndClosesWhenCodeDisappears()
    {
        var releases = 0;
        var polls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal)) releases++;
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/rooms/status", StringComparison.Ordinal));
            return Task.FromResult(Interlocked.Increment(ref polls) == 1 ? Json(new { status = Status() }) :
                Json(new { error = "room_not_found" }, HttpStatusCode.NotFound));
        }));
        await using var lease = new P2pRoomLease(new RelayApi(http, "3.2.1"), Room, false, _ => Task.FromResult(true), TimeSpan.FromMilliseconds(25));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.Closed += _ => closed.TrySetResult();
        await lease.StartAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await lease.DisposeAsync();
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task DisposalDuringCloudStartupWaitsAndCancelsHeartbeat()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/ready", StringComparison.Ordinal))
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            else Interlocked.Increment(ref releases);
            return Json(new { ok = true });
        }));
        var lease = new P2pRoomLease(new RelayApi(http, "3.2.1"), Room, true, _ => Task.FromResult(true));
        var startup = lease.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<OperationCanceledException>(() => startup);
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public async Task HostClosesAfterThreeMissingGameChecksEvenWhenReadyApiIsUnavailable()
    {
        var readyStates = new ConcurrentQueue<bool>();
        var checks = 0;
        var releases = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
            { Interlocked.Increment(ref releases); return Json(new { ok = true }); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return Json(new { status = Status() });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var ready = body.RootElement.GetProperty("ready").GetBoolean();
            readyStates.Enqueue(ready);
            return ready ? Json(new { ok = true }) : Json(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
        }));
        await using var lease = new P2pRoomLease(new RelayApi(http, "3.2.1"), Room, true,
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(false); }, TimeSpan.FromMilliseconds(25));
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.Closed += message => closed.TrySetResult(message);
        await lease.StartAsync();
        Assert.AreEqual("游戏房间已关闭", await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await lease.DisposeAsync();
        Assert.AreEqual(3, checks, "A cloud outage prevented the local missing-game threshold from being reached.");
        Assert.IsTrue(readyStates.Count >= 2 && readyStates.Count < 4,
            "Cloud retries were not backed off independently from local checks.");
        Assert.IsTrue(readyStates.First());
        Assert.IsTrue(readyStates.Skip(1).All(ready => !ready));
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public async Task ExistingGuestStatusDoesNotUseJoinMenuVersionOrRegionGates()
    {
        var polls = 0;
        var releases = 0;
        var thirdPollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishThirdPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
            { Interlocked.Increment(ref releases); return Json(new { ok = true }); }
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/rooms/status", StringComparison.Ordinal),
                "Existing sessions must never call the new-entry Join endpoint.");
            var count = Interlocked.Increment(ref polls);
            if (count == 3)
            {
                thirdPollEntered.TrySetResult();
                await finishThirdPoll.Task.WaitAsync(ct);
            }
            return count <= 3 ? Json(new { status = Status() }) :
                Json(new { error = "room_not_found" }, HttpStatusCode.NotFound);
        }));
        await using var lease = new P2pRoomLease(new RelayApi(http, "3.2.1"), Room, false,
            _ => Task.FromResult(true), TimeSpan.FromMilliseconds(25));
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.Closed += message => closed.TrySetResult(message);
        await lease.StartAsync();
        try
        {
            await thirdPollEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(closed.Task.IsCompleted, "An existing P2P guest was closed by a new-entry policy change.");
            finishThirdPoll.TrySetResult();
            Assert.AreEqual("房间码无效或房间已关闭", await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await lease.DisposeAsync();
            Assert.AreEqual(4, polls);
            Assert.AreEqual(0, releases, "Guest cleanup released the owner's cloud room.");
        }
        finally { finishThirdPoll.TrySetResult(); }
    }

    [TestMethod]
    [DataRow("{\"error\":\"banned\"}")]
    [DataRow("<html>Forbidden</html>")]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("{\"error\":null}")]
    [DataRow("{\"error\":404}")]
    [DataRow("null")]
    [DataRow("[]")]
    public async Task GuestClosesForBannedOrMalformedForbiddenResponses(string errorBody)
    {
        var polls = 0;
        var releases = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
                Interlocked.Increment(ref releases);
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/rooms/status", StringComparison.Ordinal));
            if (Interlocked.Increment(ref polls) == 1) return Task.FromResult(Json(new { status = Status() }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent(errorBody) });
        }));
        await using var lease = new P2pRoomLease(new RelayApi(http, "3.2.1"), Room, false,
            _ => Task.FromResult(true), TimeSpan.FromMilliseconds(25));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.Closed += _ => closed.TrySetResult();
        await lease.StartAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await lease.DisposeAsync();
        Assert.AreEqual(2, polls, "An unexpected 403 body was ignored as an existing-connection exception.");
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task StatusApiUsesIndependentPostRoomIdAndDiagnosticsWithoutNetworkIdentity()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/api/v1/p2p/rooms/status", StringComparison.Ordinal));
            Assert.AreEqual("fixture-session", request.Headers.Authorization?.Parameter);
            var raw = await request.Content!.ReadAsStringAsync(ct);
            using var body = JsonDocument.Parse(raw);
            Assert.AreEqual(Room.Id, body.RootElement.GetProperty("id").GetString());
            Assert.AreEqual("reconnecting", body.RootElement.GetProperty("phase").GetString());
            Assert.AreEqual("connection_lost", body.RootElement.GetProperty("failure_code").GetString());
            Assert.AreEqual(2, body.RootElement.GetProperty("retry_count").GetInt32());
            Assert.IsFalse(raw.Contains(Room.NetworkName, StringComparison.Ordinal));
            Assert.IsFalse(raw.Contains(Room.NetworkSecret, StringComparison.Ordinal));
            return Json(new { status = Status() });
        }));
        var api = new RelayApi(http, "3.2.1") { Token = "fixture-session" };
        Assert.AreEqual(Status(), await api.GetP2pRoomStatusAsync(Room.Id, "reconnecting", "connection_lost", 2));
    }

    [TestMethod]
    public async Task GuestCloudOutageRaisesSafeHintBacksOffAndClearsHintAfterRecovery()
    {
        var polls = 0;
        var times = new ConcurrentQueue<TimeSpan>();
        var clock = Stopwatch.StartNew();
        var notices = new ConcurrentQueue<string?>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/rooms/status", StringComparison.Ordinal));
            times.Enqueue(clock.Elapsed);
            var count = Interlocked.Increment(ref polls);
            return Task.FromResult(count is 2 or 3 ? Json(new { error = "upstream_unavailable", detail = "private network token" }, HttpStatusCode.ServiceUnavailable) :
                Json(new { status = Status() }));
        }));
        await using var lease = Guest(http);
        var closed = Closed(lease);
        lease.Changed += notice => { notices.Enqueue(notice); if (notice is null) recovered.TrySetResult(); };
        await lease.StartAsync();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(closed.Task.IsCompleted);
        Assert.AreEqual(Status(), lease.LastStatus);
        Assert.IsTrue(notices.First()!.Contains("重试", StringComparison.Ordinal));
        Assert.IsFalse(notices.Any(message => message?.Contains("private network token", StringComparison.Ordinal) == true));
        var requests = times.ToArray();
        Assert.IsTrue(requests[2] - requests[1] >= TimeSpan.FromMilliseconds(45), "First failure did not back off.");
        Assert.IsTrue(requests[3] - requests[2] >= TimeSpan.FromMilliseconds(95), "Repeated failures did not increase retry delay.");
        Assert.IsNull(notices.Last());
    }

    [TestMethod]
    public async Task UnreadyRoomAndUnavailableNodeRemainTemporaryUntilReadyRestores()
    {
        var polls = 0;
        var notices = new ConcurrentQueue<string?>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { status =
            Interlocked.Increment(ref polls) switch { 2 => Status(ready: false), 3 => Status(available: false), _ => Status() } }))));
        await using var lease = Guest(http);
        var closed = Closed(lease);
        lease.Changed += value => { notices.Enqueue(value); if (value is null) recovered.TrySetResult(); };
        await lease.StartAsync();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(closed.Task.IsCompleted);
        Assert.IsTrue(notices.Any(message => message?.Contains("房主", StringComparison.Ordinal) == true));
        Assert.IsTrue(notices.Any(message => message?.Contains("节点", StringComparison.Ordinal) == true));
        Assert.IsNull(notices.Last());
    }

    [TestMethod]
    [DataRow("new-id", "room")]
    [DataRow("room", "new-generation")]
    public async Task ChangedStatusIdentityOrGenerationClosesGuest(string id, string generation)
    {
        var polls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { status =
            Interlocked.Increment(ref polls) == 1 ? Status() : Status() with { Id = id, Generation = generation } }))));
        await using var lease = Guest(http);
        var closed = Closed(lease);
        await lease.StartAsync();
        Assert.AreEqual("房间已更新，请重新加入", await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    [DataRow("<html>Bad gateway</html>")]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"status\":null}")]
    [DataRow("{\"status\":{}}")]
    public async Task MalformedSuccessfulStatusIsTransientAndCanRecover(string response)
    {
        var polls = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref polls) == 2 ?
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) } : Json(new { status = Status() }))));
        await using var lease = Guest(http);
        var closed = Closed(lease);
        lease.Changed += value => { if (value is null) recovered.TrySetResult(); };
        await lease.StartAsync();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(closed.Task.IsCompleted);
    }

    [TestMethod]
    public async Task ReportUsesLatestSnapshotAndClampsRetries()
    {
        var polls = 0;
        var reported = new TaskCompletionSource<(string?, string?, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (Interlocked.Increment(ref polls) > 1)
                reported.TrySetResult((body.RootElement.GetProperty("phase").GetString(),
                    body.RootElement.GetProperty("failure_code").GetString(), body.RootElement.GetProperty("retry_count").GetInt32()));
            return Json(new { status = Status() });
        }));
        await using var lease = Guest(http);
        lease.Report("connecting", "none", 1);
        lease.Report("reconnecting", "connection_lost", 999);
        await lease.StartAsync();
        Assert.AreEqual(("reconnecting", "connection_lost", 99), await reported.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task GuestExpiredStatusSessionClosesWithoutNewJoinOrOwnerRelease()
    {
        var polls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/rooms/status", StringComparison.Ordinal));
            return Task.FromResult(Interlocked.Increment(ref polls) == 1 ? Json(new { status = Status() }) :
                Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized));
        }));
        await using var lease = Guest(http);
        var closed = Closed(lease);
        await lease.StartAsync();
        var message = await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(message.Contains("登录", StringComparison.Ordinal));
        await lease.DisposeAsync();
        Assert.AreEqual(2, polls);
    }

    [TestMethod]
    public async Task GuestDisposalCancelsPendingStatusAndDoesNotReleaseOwner()
    {
        var polls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/rooms/status", StringComparison.Ordinal));
            if (Interlocked.Increment(ref polls) > 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
            return Json(new { status = Status() });
        }));
        await using var lease = Guest(http);
        await lease.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(cancelled);
        Assert.AreEqual(2, polls);
    }

    [TestMethod]
    public async Task UnsupportedOldCloudStatusExplainsUpgradeAndNeverFallsBackToJoin()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/rooms/status", StringComparison.Ordinal));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") });
        }));
        var error = await Assert.ThrowsAsync<RelayApiException>(() => new RelayApi(http, "3.2.1").GetP2pRoomStatusAsync(Room.Id));
        Assert.IsTrue(error.Message.Contains("更新云端", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HostTransportRecoveryPausesReadinessWithoutCountingGameAsClosed()
    {
        var transportReady = false;
        var checks = 0;
        var states = new ConcurrentQueue<bool>();
        var fourthPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/ready", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var ready = body.RootElement.GetProperty("ready").GetBoolean();
                states.Enqueue(ready);
                if (!ready && states.Count >= 4) fourthPaused.TrySetResult();
                if (ready) restored.TrySetResult();
                return Json(new { ok = true });
            }
            if (route.EndsWith("/status", StringComparison.Ordinal)) return Json(new { status = Status(ready: Volatile.Read(ref transportReady)) });
            return Json(new { ok = true });
        }));
        await using var lease = new P2pRoomLease(new RelayApi(http, "3.2.1"), Room, true,
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(true); }, TimeSpan.FromMilliseconds(25),
            () => Volatile.Read(ref transportReady));
        var closed = Closed(lease);
        await lease.StartAsync();
        await fourthPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(closed.Task.IsCompleted, "Transport loss counted as a locally closed game.");
        Assert.IsTrue(checks >= 3);
        Assert.IsTrue(states.All(ready => !ready));
        Assert.AreEqual(Room.Code, lease.Room.Code);
        Volatile.Write(ref transportReady, true);
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(closed.Task.IsCompleted);
        Assert.AreEqual(Room.Code, lease.Room.Code);
    }

    private static P2pRoomLease Guest(HttpClient http) =>
        new(new RelayApi(http, "3.2.1"), Room, false, _ => Task.FromResult(true), TimeSpan.FromMilliseconds(25));
    private static TaskCompletionSource<string> Closed(P2pRoomLease lease)
    { var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); lease.Closed += value => closed.TrySetResult(value); return closed; }
    private static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(data)) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handler(request, ct);
    }
}
