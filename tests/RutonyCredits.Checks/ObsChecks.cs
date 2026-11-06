using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RutonyCredits;

public static class ObsChecks
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "rutony-obs-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("OBS check timed out"); await Task.Delay(10); }
    }
    public static async Task Run()
    {
        await using var fake = await FakeObs.Start();
        var time = new MonoTimeProvider();
        var directory = Temp(); int enter = 0, leave = 0, started = 0, stopped = 0, warnings = 0;
        await using (var controller = new ObsController(directory,
            () => { Interlocked.Increment(ref enter); return Task.CompletedTask; },
            () => { Interlocked.Increment(ref leave); return Task.CompletedTask; },
            () => { Interlocked.Increment(ref started); return Task.CompletedTask; },
            () => { Interlocked.Increment(ref stopped); return Task.CompletedTask; }, _ => Interlocked.Increment(ref warnings), time))
        {
            Assert(!controller.Status.Options.Enabled && !controller.Status.Connected, "OBS must start disabled");
            try { await controller.ConfigureAsync(new() { Enabled = true, Port = 0 }); throw new Exception("Invalid port accepted"); } catch (ArgumentException) { }
            var options = new ObsOptions { Enabled = true, Port = fake.Port, SceneName = "Credits" };
            await controller.ConfigureAsync(options, OperatingSystem.IsWindows() ? FakeObs.Password : null);
            var peer = await fake.NextPeer(); await Until(() => controller.Status.Connected);
            Assert(fake.AuthValid, "OBS challenge authentication is incorrect");
            Assert((fake.Subscription & 68) == 68, "Required scene/output subscriptions missing");
            Assert(controller.Status.Scenes.Contains("Credits"), "Scene list was not loaded");
            Assert(enter == 0 && leave == 0 && started == 0 && stopped == 0, "Initial baseline triggered automation");
            await peer.Event("CurrentPreviewSceneChanged", new { sceneName = "Live" });
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Credits" });
            await fake.Barrier(controller, peer);
            Assert(enter == 0 && leave == 0, "Preview or duplicate Program triggered playback");
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Live" });
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Credits" });
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Credits" });
            await fake.Barrier(controller, peer);
            Assert(enter == 1 && leave == 1, "Program transitions were not idempotent");
            await peer.Stream("RECONNECTING"); await peer.Stream("RECONNECTED");
            await fake.Barrier(controller, peer);
            Assert(started == 0 && stopped == 0, "Network reconnect mutated the session");
            await peer.Stream("STARTING"); await peer.Stream("STARTED"); await peer.Stream("STARTED");
            await fake.Barrier(controller, peer);
            Assert(started == 1 && stopped == 0, "Actual stream start was not deduplicated");
            await peer.Stream("STOPPED"); await peer.Stream("STOPPED");
            await fake.Barrier(controller, peer);
            Assert(stopped == 0 && warnings == 1, "Unexpected output failure reset data or repeated its warning");
            await peer.Stream("STARTING"); await peer.Stream("STARTED"); await peer.Stream("STOPPING");
            await fake.Barrier(controller, peer); Assert(stopped == 0, "Reset occurred before completed stop");
            await peer.Stream("STOPPED"); await peer.Stream("STOPPED");
            await fake.Barrier(controller, peer); Assert(stopped == 0, "Confirmed stop must retain data during the reset delay");
            Assert(controller.Status.PendingResetUntilUtc == time.GetUtcNow().AddMinutes(5), "Pending deadline missing");
            time.Advance(TimeSpan.FromMinutes(5));
            await Until(() => stopped == 1 && controller.Status.PendingResetUntilUtc == null);
            await peer.Stream("STARTING"); await peer.Stream("STARTED"); await peer.Stream("STOPPING"); await peer.Stream("RECONNECTING"); await peer.Stream("STOPPED");
            await fake.Barrier(controller, peer); Assert(stopped == 1, "Reconnecting did not disarm stop");
            await peer.Stream("STARTING"); await peer.Stream("STARTED"); await peer.Stream("STOPPING");
            await fake.Barrier(controller, peer); fake.InitialStreaming = false; peer.Abort();
            peer = await fake.NextPeer(); await Until(() => controller.Status.Connected);
            await fake.Barrier(controller, peer); Assert(stopped == 1, "Connection loss/restart completed a stale armed stop");
            int beforeEnter = enter, beforeLeave = leave;
            await controller.ConfigureAsync(options with { AutoStart = false, ResetOnStop = false });
            peer = await fake.NextPeer(); await Until(() => controller.Status.Connected);
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Live" });
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Credits" });
            await peer.Stream("STARTING"); await peer.Stream("STARTED"); await peer.Stream("STOPPING"); await peer.Stream("STOPPED");
            await fake.Barrier(controller, peer);
            Assert(enter == beforeEnter && leave == beforeLeave && stopped == 1, "Disabled automation still ran");
            if (OperatingSystem.IsWindows())
            {
                var configFile = Path.Combine(directory, "obs.json");
                var attributes = File.GetAttributes(configFile); File.SetAttributes(configFile, attributes | FileAttributes.ReadOnly);
                try
                {
                    try { await controller.ConfigureAsync(options with { Port = 1 }, "replacement-that-must-not-commit"); throw new Exception("Read-only save unexpectedly succeeded"); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                finally { File.SetAttributes(configFile, attributes); }
                Assert(controller.Status.Options.Port == fake.Port && controller.Status.Connected, "Failed save changed active configuration");
                // Reopening the protected file proves password rollback, not merely memory state.
                await using (var checkSaved = new ObsController(directory, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
                {
                    _ = await fake.NextPeer(); await Until(() => checkSaved.Status.Connected);
                    Assert(fake.AuthValid, "Failed save replaced the protected password");
                }
                Assert(controller.Status.HasPassword, "Blank update did not preserve password");
                Assert(!File.ReadAllText(Path.Combine(directory, "obs.json")).Contains(FakeObs.Password, StringComparison.Ordinal), "Password leaked into options JSON");
                Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "obs-secret.bin"))).Contains(FakeObs.Password, StringComparison.Ordinal), "Password stored in plaintext");
                Assert(!JsonSerializer.Serialize(controller.Status).Contains(FakeObs.Password, StringComparison.Ordinal), "Password leaked into status");
            }
            await controller.ConfigureAsync(options with { Enabled = false }, clearPassword: true);
            Assert(!controller.Status.HasPassword && !controller.Status.Connected, "Disable/clear did not complete");
            int connections = fake.Connections; await Task.Delay(100);
            Assert(fake.Connections == connections, "Disabled client reconnected");
        }
        await using (var restored = new ObsController(directory, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
            Assert(!restored.Status.Options.Enabled && !restored.Status.HasPassword && restored.Status.Options.Port == fake.Port, "Options/clear did not persist");

        if (OperatingSystem.IsWindows())
        {
            await using var wrong = new ObsController(Temp(), () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
            await wrong.ConfigureAsync(new() { Enabled = true, Port = fake.Port, SceneName = "Credits" }, "synthetic-wrong-value");
            await Until(() => wrong.Status.Error != null);
            Assert(!wrong.Status.Connected && !wrong.Status.Error!.Contains("synthetic-wrong-value", StringComparison.Ordinal) && !wrong.Status.Error.Contains("RAW_SERVER_DETAIL", StringComparison.Ordinal), "Auth error was missing or leaked data");
        }
        fake.InterleaveScene = "Live";
        await using (var interleaved = new ObsController(Temp(), () => { Interlocked.Increment(ref enter); return Task.CompletedTask; }, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
        {
            int baselineEnter = enter;
            await interleaved.ConfigureAsync(new() { Enabled = true, Port = fake.Port, SceneName = "Credits" }, OperatingSystem.IsWindows() ? FakeObs.Password : null);
            var peer = await fake.NextPeer(); await Until(() => interleaved.Status.Connected);
            Assert(enter == baselineEnter, "Interleaved initialization event triggered playback");
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Credits" });
            await fake.Barrier(interleaved, peer);
            Assert(enter == baselineEnter + 1, "A stale query overwrote the latest baseline scene");
        }
        fake.InterleaveScene = null; fake.FailRequest = true;
        await using (var rejected = new ObsController(Temp(), () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
        {
            await rejected.ConfigureAsync(new() { Enabled = true, Port = fake.Port, AutoStart = false }, OperatingSystem.IsWindows() ? FakeObs.Password : null);
            await Until(() => rejected.Status.Error != null);
            Assert(!rejected.Status.Connected && !rejected.Status.Error!.Contains("RAW_SERVER_DETAIL", StringComparison.Ordinal), "Rejected request error was unsafe");
        }
        fake.FailRequest = false;
        await using (var discovery = new ObsController(Temp(), () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
        {
            await discovery.ConfigureAsync(new() { Enabled = true, Port = fake.Port }, OperatingSystem.IsWindows() ? FakeObs.Password : null);
            _ = await fake.NextPeer(); await Until(() => discovery.Status.Connected);
            Assert(discovery.Status.Scenes.Contains("Credits") && discovery.Status.Phase.Contains("Выберите", StringComparison.Ordinal), "First connection cannot discover scenes before selecting one");
        }
        var enteredBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var draining = new ObsController(Temp(), () => { enteredBarrier.TrySetResult(); return releaseBarrier.Task; }, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
        {
            var options = new ObsOptions { Enabled = true, Port = fake.Port, SceneName = "Credits" };
            await draining.ConfigureAsync(options, OperatingSystem.IsWindows() ? FakeObs.Password : null);
            var peer = await fake.NextPeer(); await Until(() => draining.Status.Connected);
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Live" });
            await peer.Event("CurrentProgramSceneChanged", new { sceneName = "Credits" });
            await enteredBarrier.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var disable = draining.ConfigureAsync(options with { Enabled = false });
            try { await Task.Delay(80); Assert(!disable.IsCompleted, "Disable acknowledged while an already started store barrier could still mutate state"); }
            finally { releaseBarrier.TrySetResult(); }
            await disable.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!draining.Status.Connected, "Drain did not finish disabling");
        }
        await using var stalled = await FakeObs.Start(holdHello: true);
        var pending = new ObsController(Temp(), () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
        await pending.ConfigureAsync(new() { Enabled = true, Port = stalled.Port, AutoStart = false });
        await Until(() => stalled.Connections > 0);
        var watch = Stopwatch.StartNew(); await pending.DisposeAsync();
        Assert(watch.Elapsed < TimeSpan.FromSeconds(3), "Shutdown hung waiting for handshake");
    }

    internal sealed class FakeObs : IAsyncDisposable
    {
        public const string Password = "synthetic-obs-test-password";
        private readonly WebApplication app;
        private readonly bool holdHello;
        private readonly Channel<Peer> peers = Channel.CreateUnbounded<Peer>();
        private readonly CancellationTokenSource lifetime = new();
        private int marker;
        public int Port { get; private set; }
        public int Connections;
        public bool InitialStreaming = true;
        public string InitialScene = "Credits";
        public string? InterleaveScene;
        public bool FailRequest;
        public bool Reconnecting;
        public bool? FreshActive;
        public bool? FreshReconnecting;
        public bool IgnoreFreshStatus;
        public bool MalformedFreshStatus;
        public int StatusRequests;
        public bool AuthValid;
        public int Subscription;
        private FakeObs(WebApplication app, bool holdHello) { this.app = app; this.holdHello = holdHello; }
        public static async Task<FakeObs> Start(bool holdHello = false)
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); var fake = new FakeObs(app, holdHello);
            app.UseWebSockets(); app.Map("/", fake.Handle);
            await app.StartAsync(); fake.Port = new Uri(app.Urls.Single()).Port; return fake;
        }
        private async Task Handle(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync(); Interlocked.Increment(ref Connections);
            var peer = new Peer(socket, this); var token = lifetime.Token;
            try
            {
                if (holdHello) { await Task.Delay(Timeout.Infinite, token); return; }
                var hello = OperatingSystem.IsWindows() ? (object)new { rpcVersion = 1, authentication = new { salt = "fake-salt", challenge = "fake-challenge" } } : new { rpcVersion = 1 };
                await peer.Send(new { op = 0, d = hello });
                using var identify = await Read(socket, token); var data = identify.RootElement.GetProperty("d");
                Subscription = data.GetProperty("eventSubscriptions").GetInt32();
                var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Password + "fake-salt")));
                var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(hash + "fake-challenge")));
                AuthValid = !OperatingSystem.IsWindows() || data.TryGetProperty("authentication", out var auth) && auth.GetString() == expected;
                if (!AuthValid) { await socket.CloseAsync((WebSocketCloseStatus)4009, "RAW_SERVER_DETAIL", token); return; }
                await peer.Send(new { op = 2, d = new { negotiatedRpcVersion = 1 } });
                int requests = 0;
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var message = await Read(socket, token);
                    if (message.RootElement.GetProperty("op").GetInt32() != 6) continue;
                    var request = message.RootElement.GetProperty("d");
                    string type = request.GetProperty("requestType").GetString()!;
                    if (InterleaveScene != null && type == "GetCurrentProgramScene") await peer.Event("CurrentProgramSceneChanged", new { sceneName = InterleaveScene });
                    if (type == "GetStreamStatus")
                    {
                        Interlocked.Increment(ref StatusRequests);
                        if (requests >= 3 && IgnoreFreshStatus) continue;
                    }
                    object response = type switch
                    {
                        "GetSceneList" => new { currentProgramSceneName = InitialScene, scenes = new[] { new { sceneName = "Live" }, new { sceneName = "Credits" } } },
                        "GetCurrentProgramScene" => new { currentProgramSceneName = InitialScene },
                        "GetStreamStatus" when requests >= 3 && MalformedFreshStatus => new { outputActive = false },
                        "GetStreamStatus" => new { outputActive = requests >= 3 ? FreshActive ?? InitialStreaming : InitialStreaming, outputReconnecting = requests >= 3 ? FreshReconnecting ?? Reconnecting : Reconnecting },
                        _ => throw new Exception("Unexpected request")
                    };
                    await peer.Send(new { op = 7, d = new { requestType = type, requestId = request.GetProperty("requestId").GetString(), requestStatus = new { result = !FailRequest, code = FailRequest ? 500 : 100, comment = "RAW_SERVER_DETAIL" }, responseData = response } });
                    if (++requests == 3) peers.Writer.TryWrite(peer);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or JsonException) { }
        }
        public async Task<Peer> NextPeer() => await peers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        public async Task Barrier(ObsController controller, Peer peer)
        {
            string name = "marker-" + Interlocked.Increment(ref marker);
            await peer.Event("SceneListChanged", new { scenes = new[] { new { sceneName = "Credits" }, new { sceneName = name } } });
            await Until(() => controller.Status.Scenes.Contains(name));
        }
        public async ValueTask DisposeAsync() { lifetime.Cancel(); await app.StopAsync(); await app.DisposeAsync(); lifetime.Dispose(); }
        private static async Task<JsonDocument> Read(WebSocket socket, CancellationToken token)
        {
            using var stream = new MemoryStream(); var buffer = new byte[4096]; WebSocketReceiveResult result;
            do { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token); if (result.MessageType == WebSocketMessageType.Close) throw new IOException(); stream.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
            return JsonDocument.Parse(stream.ToArray());
        }
        public sealed class Peer(WebSocket socket, FakeObs owner)
        {
            private readonly SemaphoreSlim send = new(1, 1);
            public async Task Send(object message)
            {
                await send.WaitAsync();
                try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None); }
                finally { send.Release(); }
            }
            public Task Event(string type, object data) => Send(new { op = 5, d = new { eventType = type, eventData = data } });
            public Task Stream(string state)
            {
                owner.InitialStreaming = state is "STARTING" or "STARTED" or "RECONNECTED" or "RECONNECTING" or "STOPPING";
                owner.Reconnecting = state == "RECONNECTING";
                return Event("StreamStateChanged", new { outputState = "OBS_WEBSOCKET_OUTPUT_" + state, outputActive = state is "STARTED" or "RECONNECTED" });
            }
            public void Abort() => socket.Abort();
        }
    }
}
