using System;
using System.Collections.Concurrent;
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
            else Interlocked.Increment(ref released);
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
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal)) releases++;
            return Task.FromResult(Json(new { error = "room_not_found" }, HttpStatusCode.NotFound));
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
        CollectionAssert.AreEqual(new[] { true, false, false }, readyStates.ToArray(),
            "The third local failure should close the room before another cloud request.");
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    [DataRow("p2p_disabled")]
    [DataRow("version_too_low")]
    public async Task ExistingGuestSurvivesMenuOrVersionPolicyChangesButClosesWhenRoomDisappears(string policyError)
    {
        var polls = 0;
        var releases = 0;
        var thirdPollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishThirdPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
            { Interlocked.Increment(ref releases); return Json(new { ok = true }); }
            var count = Interlocked.Increment(ref polls);
            if (count == 3)
            {
                thirdPollEntered.TrySetResult();
                await finishThirdPoll.Task.WaitAsync(ct);
            }
            return count <= 3 ? Json(new { error = policyError }, HttpStatusCode.Forbidden) :
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
            else Interlocked.Increment(ref polls);
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
        Assert.AreEqual(1, polls, "An unexpected 403 body was ignored as an existing-connection exception.");
        Assert.AreEqual(0, releases);
    }

    private static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(data)) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handler(request, ct);
    }
}
