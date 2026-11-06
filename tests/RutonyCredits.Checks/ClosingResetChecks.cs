using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using RutonyCredits;

internal static class ClosingResetChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<Task<bool>> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("Closing reset check timed out");
            await Task.Delay(10);
        }
    }

    public static async Task RuntimeClosing()
    {
        // Each case uses the real HTTP/controller/store lifecycle and isolated data.
        foreach (var scenario in new[] { "no-stop", "unexpected-stop", "resume", "loss", "manual", "settings", "expired", "pending", "saturated" })
        {
            await using var fake = await ObsChecks.FakeObs.Start();
            var directory = Path.Combine(Path.GetTempPath(), "credits-closing-" + Guid.NewGuid().ToString("N"));
            var package = Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyCredits");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var time = new MonoTimeProvider();
            RuntimeHost? runtime = RuntimeHost.StartWithTimeProvider(package, directory, port, time);
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
            async Task<CreditState> State() => (await http.GetFromJsonAsync<CreditState>("/api/state", Wire.Json))!;
            async Task<ObsStatus> Obs() => (await http.GetFromJsonAsync<ObsStatus>("/api/obs", Wire.Json))!;
            try
            {
                runtime.Record("twitch", "follower", "Before stop");
                runtime.Record("twitch", "subscriber", "Another participant");
                var initial = await State();
                var html = await http.GetStringAsync("/control");
                var token = Regex.Match(html, "name=\"credits-token\" content=\"([A-F0-9]+)\"").Groups[1].Value;
                http.DefaultRequestHeaders.Add("X-Credits-Token", token);
                var options = new ObsOptions { Enabled = true, Port = fake.Port, AutoStart = false };
                using (var response = await http.PostAsJsonAsync("/api/obs", new { options, password = OperatingSystem.IsWindows() ? ObsChecks.FakeObs.Password : null }))
                    response.EnsureSuccessStatusCode();
                var peer = await fake.NextPeer();
                await Until(async () => (await Obs()).Connected);
                if (scenario != "no-stop")
                {
                    if (scenario != "unexpected-stop") await peer.Stream("STOPPING");
                    await peer.Stream("STOPPED");
                    if (scenario != "unexpected-stop") await Until(async () => (await Obs()).PendingResetUntilUtc != null);
                }
                if (scenario == "resume") await peer.Stream("STARTING");
                else if (scenario == "loss")
                {
                    peer.Abort();
                    await Until(async () => !(await Obs()).Connected && (await Obs()).PendingResetUntilUtc == null);
                }
                else if (scenario == "manual")
                {
                    using var response = await http.PostAsJsonAsync("/api/reset", new { sessionId = initial.SessionId });
                    response.EnsureSuccessStatusCode();
                    runtime.Record("twitch", "follower", "New manual period");
                }
                else if (scenario == "settings")
                {
                    using var response = await http.PostAsJsonAsync("/api/obs", new { options = options with { Enabled = false } });
                    response.EnsureSuccessStatusCode();
                }
                else if (scenario == "expired")
                {
                    time.Advance(TimeSpan.FromMinutes(5));
                    await Until(async () => (await State()).SessionId != initial.SessionId && (await Obs()).PendingResetUntilUtc == null);
                    runtime.Record("twitch", "follower", "New completed period");
                }
                else if (scenario == "pending") time.Advance(TimeSpan.FromSeconds(299));
                if (scenario is "resume" or "unexpected-stop")
                {
                    // A receiver barrier: all preceding stream events have been applied.
                    await peer.Event("SceneListChanged", new { scenes = new[] { new { sceneName = "closing-marker" } } });
                    await Until(async () => (await Obs()).Scenes.Contains("closing-marker"));
                }
                var beforeClosing = await State();
                if (scenario is not ("pending" or "saturated")) Check((await Obs()).PendingResetUntilUtc == null, "Cancellation missing: " + scenario);
                if (scenario == "saturated")
                {
                    var store = (CreditStore)typeof(RuntimeHost).GetField("store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
                    var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
                    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                    queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
                    Check(entered.Wait(TimeSpan.FromSeconds(5)), "Worker barrier not entered");
                    var reads = Enumerable.Range(0, 256).Select(_ => store.StateAsync()).ToArray();
                    var closing = Task.Run(runtime.Dispose);
                    try { await Task.Delay(100); Check(!closing.IsCompleted, "Saturated close skipped drain"); }
                    finally { release.Set(); }
                    await closing; await Task.WhenAll(reads);
                }
                else await UiThreadCheck.Run(runtime.Dispose, "Closing reset: " + scenario);
                runtime.Dispose(); runtime = null;
                time.Advance(TimeSpan.FromDays(1));
                await using var restored = new CreditStore(directory);
                var after = await restored.StateAsync();
                if (scenario is "pending" or "saturated")
                {
                    Check(after.SessionId != initial.SessionId && after.EventCount == 0, "Closing during pending OBS reset retained counters instead of finishing the period");
                }
                else Check(after.SessionId == beforeClosing.SessionId && after.EventCount == beforeClosing.EventCount,
                    "Closing discarded data without an active pending reset: " + scenario);
                Check(File.ReadLines(Path.Combine(directory, "sessions", initial.SessionId + ".jsonl")).Count() == 2,
                    "Closing altered the original journal: " + scenario);
            }
            finally { runtime?.Dispose(); }
        }
    }

    public static async Task QueuedReset()
    {
        foreach (bool replacePeriod in new[] { false, true })
        {
            await using var fake = await ObsChecks.FakeObs.Start();
            var directory = Path.Combine(Path.GetTempPath(), "credits-closing-queue-" + Guid.NewGuid().ToString("N"));
            await using var store = new CreditStore(directory);
            store.Record(new("rutony", "twitch", "follower", "Original"));
            var original = await store.StateAsync();
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var time = new MonoTimeProvider();
            await using var controller = new ObsController(directory,
                () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                async () => (await store.StateAsync()).SessionId,
                (session, permission) =>
                {
                    var result = store.ResetIfCurrentAsync(session, permission);
                    queued.TrySetResult(); return result;
                }, _ => { }, time);
            await controller.ConfigureAsync(new() { Enabled = true, Port = fake.Port }, OperatingSystem.IsWindows() ? ObsChecks.FakeObs.Password : null);
            var peer = await fake.NextPeer();
            await Until(() => Task.FromResult(controller.Status.Connected));
            await peer.Stream("STOPPING"); await peer.Stream("STOPPED");
            await fake.Barrier(controller, peer);
            Check(controller.Status.PendingResetUntilUtc != null, "Queue race did not schedule a reset");
            var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(8)); });
            Check(entered.Wait(TimeSpan.FromSeconds(3)), "Closing queue blocker not entered");
            Task<string>? replacement = null;
            Task<string?>? closing = null;
            try
            {
                if (replacePeriod)
                {
                    replacement = store.ResetAsync(original.SessionId);
                    store.Record(new("rutony", "twitch", "follower", "New manual period"));
                }
                time.Advance(TimeSpan.FromMinutes(5));
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));
                closing = controller.CloseAsync().AsTask();
                await Until(() => Task.FromResult(controller.Status.PendingResetUntilUtc == null));
                Check(!closing.IsCompleted, "Closing acknowledged while a queued timer could still mutate the period");
            }
            finally { release.Set(); }
            var captured = await closing!.WaitAsync(TimeSpan.FromSeconds(3));
            Check(captured == original.SessionId, "Closing lost the captured pending period");
            await store.ResetIfCurrentAsync(captured!, CancellationToken.None);
            Check(await controller.CloseAsync() == null, "Repeated closing returned another reset authorization");
            var current = await store.StateAsync();
            if (replacePeriod)
                Check(current.SessionId == await replacement! && current.EventCount == 1, "Closing reset the replacement period");
            else Check(current.SessionId != original.SessionId && current.EventCount == 0, "Closing failed to finish the queued timer period");
            Check(Directory.GetFiles(Path.Combine(directory, "sessions"), "*.jsonl").Length == 2, "Timer and closing reset twice");
            Check(File.ReadLines(Path.Combine(directory, "sessions", original.SessionId + ".jsonl")).Count() == 1, "Closing changed the original archive");
        }
    }


    public static async Task UnsafeFreshStateDuringClosing()
    {
        foreach (var scenario in new[] { "active", "reconnecting", "error" })
        {
            await using var fake = await ObsChecks.FakeObs.Start();
            var directory = Path.Combine(Path.GetTempPath(), "credits-unsafe-closing-" + Guid.NewGuid().ToString("N"));
            await using var store = new CreditStore(directory);
            store.Record(new("rutony", "twitch", "follower", "Retain"));
            var original = await store.StateAsync();
            var time = new MonoTimeProvider();
            using var warningEntered = new ManualResetEventSlim(); using var releaseWarning = new ManualResetEventSlim();
            await using var controller = new ObsController(directory,
                () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                async () => (await store.StateAsync()).SessionId, store.ResetIfCurrentAsync,
                _ => { warningEntered.Set(); releaseWarning.Wait(TimeSpan.FromSeconds(8)); }, time);
            await controller.ConfigureAsync(new() { Enabled = true, Port = fake.Port }, OperatingSystem.IsWindows() ? ObsChecks.FakeObs.Password : null);
            var peer = await fake.NextPeer(); await Until(() => Task.FromResult(controller.Status.Connected));
            await peer.Stream("STOPPING"); await peer.Stream("STOPPED"); await fake.Barrier(controller, peer);
            Check(controller.Status.PendingResetUntilUtc != null, "Unsafe close case did not schedule a reset");
            fake.FreshActive = scenario == "active";
            fake.FreshReconnecting = scenario == "reconnecting";
            fake.FailRequest = scenario == "error";
            Task<string?>? closing = null;
            var advancing = Task.Run(() => time.Advance(TimeSpan.FromMinutes(5)));
            try
            {
                Check(warningEntered.Wait(TimeSpan.FromSeconds(3)), "Unsafe fresh result did not reach retention warning: " + scenario);
                closing = controller.CloseAsync().AsTask();
                await Until(() => Task.FromResult(controller.Status.PendingResetUntilUtc == null));
            }
            finally { releaseWarning.Set(); }
            await advancing;
            var captured = await closing!.WaitAsync(TimeSpan.FromSeconds(3));
            if (captured != null) await store.ResetIfCurrentAsync(captured, CancellationToken.None);
            var current = await store.StateAsync();
            Check(current.SessionId == original.SessionId && current.EventCount == 1,
                "Closing discarded statistics after a fresh unsafe result already required retention: " + scenario);
        }
    }

}
