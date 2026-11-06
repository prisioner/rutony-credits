using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using RutonyCredits;

internal static class ObsDelayChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "credits-obs-delay-" + Guid.NewGuid().ToString("N"));
    private static async Task Until(Func<bool> test)
    {
        var watch = Stopwatch.StartNew();
        while (!test()) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("Delayed OBS check timed out"); await Task.Delay(10); }
    }
    private static async Task<ObsChecks.FakeObs.Peer> Connect(ObsController controller, ObsChecks.FakeObs fake, int minutes = 5)
    {
        await controller.ConfigureAsync(new() { Enabled = true, Port = fake.Port, ResetDelayMinutes = minutes }, OperatingSystem.IsWindows() ? ObsChecks.FakeObs.Password : null);
        var peer = await fake.NextPeer(); await Until(() => controller.Status.Connected); return peer;
    }
    private static async Task Stop(ObsController controller, ObsChecks.FakeObs fake, ObsChecks.FakeObs.Peer peer)
    {
        await peer.Stream("STARTING"); await peer.Stream("STARTED");
        await peer.Stream("STOPPING"); await peer.Stream("STOPPED");
        await fake.Barrier(controller, peer);
        Check(controller.Status.PendingResetUntilUtc != null, "Confirmed stop did not schedule a reset");
    }
    private static ObsController Controller(string directory, CreditStore store, MonoTimeProvider time, Action<string>? warn = null,
        Func<string, CancellationToken, Task<bool>>? reset = null) => new(directory,
            () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
            async () => (await store.StateAsync()).SessionId, reset ?? store.ResetIfCurrentAsync, warn ?? (_ => { }), time);

    public static async Task TimingAndFreshState()
    {
        Check(JsonSerializer.Deserialize<ObsOptions>("{\"resetOnStop\":true}", Wire.Json)!.ResetDelayMinutes == 5, "Legacy OBS defaults changed");
        foreach (int invalid in new[] { 0, -1, 121 })
        {
            try { ObsController.Validate(new() { ResetDelayMinutes = invalid }); throw new Exception("Invalid delay accepted"); }
            catch (ArgumentException) { }
        }
        ObsController.Validate(new() { ResetDelayMinutes = 1 }); ObsController.Validate(new() { ResetDelayMinutes = 120 });
        await using var fake = await ObsChecks.FakeObs.Start(); var time = new MonoTimeProvider();
        var directory = Temp(); await using var store = new CreditStore(directory);
        var original = (await store.StateAsync()).SessionId;
        store.Record(new("rutony", "twitch", "follower", "Retained"));
        int warnings = 0;
        await using var controller = Controller(directory, store, time, _ => Interlocked.Increment(ref warnings));
        var peer = await Connect(controller, fake);
        await Stop(controller, fake, peer);
        var deadline = controller.Status.PendingResetUntilUtc;
        time.MoveUtc(TimeSpan.FromDays(7));
        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await fake.Barrier(controller, peer);
        Check((await store.StateAsync()).SessionId == original && fake.StatusRequests == 1, "Wall clock or early timer cleared data");
        Check(controller.Status.PendingResetUntilUtc == deadline, "Wall clock changed displayed planned deadline");
        await peer.Stream("STARTING"); await fake.Barrier(controller, peer);
        Check(controller.Status.PendingResetUntilUtc == null, "STARTING did not cancel pending status");
        time.Advance(TimeSpan.FromSeconds(1)); await fake.Barrier(controller, peer);
        Check((await store.StateAsync()).SessionId == original, "Technical stop/start at 4:59 discarded data");
        await Stop(controller, fake, peer);
        time.MoveUtc(TimeSpan.FromDays(-14));
        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59)); await fake.Barrier(controller, peer);
        Check((await store.StateAsync()).SessionId == original, "Repeated stop did not get a full fresh delay");
        time.Advance(TimeSpan.FromSeconds(1)); await Until(() => controller.Status.PendingResetUntilUtc == null);
        var after = await store.StateAsync();
        Check(after.SessionId != original && after.EventCount == 0 && fake.StatusRequests == 2, "Expiry did not query fresh state and reset once");
        Check(File.ReadLines(Path.Combine(directory, "sessions", original + ".jsonl")).Count() == 1, "Delayed reset damaged archive");
        time.Advance(TimeSpan.FromHours(1)); await fake.Barrier(controller, peer);
        Check((await store.StateAsync()).SessionId == after.SessionId && fake.StatusRequests == 2, "Timer repeated a completed reset");

        foreach (var unsafeState in new[] { "active", "reconnecting", "error", "timeout", "malformed" })
        {
            store.Record(new("rutony", "twitch", "follower", unsafeState));
            var retained = await store.StateAsync(); int before = warnings, requests = fake.StatusRequests;
            await Stop(controller, fake, peer);
            fake.FreshActive = unsafeState == "active";
            fake.FreshReconnecting = unsafeState == "reconnecting";
            fake.FailRequest = unsafeState == "error";
            fake.IgnoreFreshStatus = unsafeState == "timeout";
            fake.MalformedFreshStatus = unsafeState == "malformed";
            time.Advance(TimeSpan.FromMinutes(5));
            await Until(() => fake.StatusRequests > requests);
            if (unsafeState == "timeout")
            {
                // WaitAsync has installed its deadline by the time the receiver handles this marker.
                await fake.Barrier(controller, peer); time.Advance(TimeSpan.FromSeconds(5));
            }
            await Until(() => controller.Status.PendingResetUntilUtc == null);
            var current = await store.StateAsync();
            Check(current.SessionId == retained.SessionId && current.EventCount == retained.EventCount, "Unsafe fresh state discarded data: " + unsafeState);
            Check(warnings > before, "Unsafe fresh state did not warn: " + unsafeState);
            Check(!(controller.Status.Error ?? "").Contains("RAW_SERVER_DETAIL"), "Fresh query error leaked server details");
            fake.FreshActive = fake.FreshReconnecting = null; fake.FailRequest = fake.IgnoreFreshStatus = fake.MalformedFreshStatus = false;
        }
        await controller.ConfigureAsync(controller.Status.Options with { ResetDelayMinutes = 17 });
        peer = await fake.NextPeer(); await Until(() => controller.Status.Connected);
        Check(controller.Status.Options.ResetDelayMinutes == 17 && File.ReadAllText(Path.Combine(directory, "obs.json")).Contains("17"), "Custom delay did not persist");
    }

    public static async Task CancellationAndRestart()
    {
        foreach (var cause in new[] { "STARTING", "STARTED", "RECONNECTING", "RECONNECTED", "configure", "disable", "manual", "loss", "dispose" })
        {
            await using var fake = await ObsChecks.FakeObs.Start(); var time = new MonoTimeProvider(); var directory = Temp();
            await using var store = new CreditStore(directory); store.Record(new("rutony", "twitch", "follower", cause));
            var original = await store.StateAsync();
            await using var controller = Controller(directory, store, time);
            var peer = await Connect(controller, fake); await Stop(controller, fake, peer);
            if (cause == "configure" || cause == "disable")
            {
                await controller.ConfigureAsync(controller.Status.Options with { Enabled = cause != "disable" });
                if (cause == "configure") { peer = await fake.NextPeer(); await Until(() => controller.Status.Connected); }
            }
            else if (cause == "dispose") await controller.DisposeAsync();
            else if (cause == "manual") await controller.CancelPendingResetAsync();
            else if (cause == "loss")
            {
                peer.Abort(); peer = await fake.NextPeer(); await Until(() => controller.Status.Connected);
            }
            else { await peer.Stream(cause); await fake.Barrier(controller, peer); }
            Check(controller.Status.PendingResetUntilUtc == null, "Pending reset survived cancellation: " + cause);
            time.Advance(TimeSpan.FromMinutes(10));
            if (cause is not ("dispose" or "disable")) await fake.Barrier(controller, peer);
            var state = await store.StateAsync();
            Check(state.SessionId == original.SessionId && state.EventCount == 1, "Cancelled timer discarded data: " + cause);
        }
        // A new period bypassing HTTP is still guarded by the captured session in the queue.
        await using (var fake = await ObsChecks.FakeObs.Start())
        {
            var time = new MonoTimeProvider(); var directory = Temp();
            await using var store = new CreditStore(directory);
            await using var controller = Controller(directory, store, time);
            var peer = await Connect(controller, fake); await Stop(controller, fake, peer);
            var newSession = await store.ResetAsync((await store.StateAsync()).SessionId);
            store.Record(new("rutony", "twitch", "follower", "New period"));
            time.Advance(TimeSpan.FromMinutes(5)); await Until(() => controller.Status.PendingResetUntilUtc == null);
            Check((await store.StateAsync()).SessionId == newSession && (await store.StateAsync()).EventCount == 1, "Old timer cleared manually replaced period");
            await Stop(controller, fake, peer);
            await controller.DisposeAsync();
            await using var restored = Controller(directory, store, time);
            peer = await fake.NextPeer(); await Until(() => restored.Status.Connected);
            time.Advance(TimeSpan.FromDays(1)); await fake.Barrier(restored, peer);
            Check(restored.Status.PendingResetUntilUtc == null && (await store.StateAsync()).SessionId == newSession, "Restart resumed a pending reset");
        }
    }

    // Exercise the actual scheduling operation at its three preparation boundaries.
    // Reflection supplies a pending cancellation callback without adding a production
    // timing hook; it reproduces generation invalidation during preparation exactly.
    private static async Task PreparationCancellation()
    {
        foreach (var boundary in new[] { "preparation", "capture-entry", "capture-await" })
        {
            var directory = Temp(); var time = new MonoTimeProvider();
            await using var store = new CreditStore(directory);
            var original = (await store.StateAsync()).SessionId;
            var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ObsController? controller = null; bool cancellingCapture = false;
            Task manualCancellation = Task.CompletedTask; string? replacement = null; int queries = 0;
            async Task ReplacePeriod()
            {
                manualCancellation = controller!.CancelPendingResetAsync();
                await manualCancellation;
                replacement = await store.ResetAsync(original);
                store.Record(new("rutony", "twitch", "follower", "Replacement during " + boundary));
            }
            async Task<string> Capture()
            {
                if (cancellingCapture && boundary == "capture-entry") await ReplacePeriod();
                if (cancellingCapture && boundary == "capture-await")
                { captureEntered.TrySetResult(); await releaseCapture.Task; }
                return (await store.StateAsync()).SessionId;
            }
            await using var subject = controller = new ObsController(directory,
                () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                Capture, store.ResetIfCurrentAsync, _ => { }, time);
            var scheduling = typeof(ObsController).GetMethod("ScheduleReset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Func<CancellationToken, Task<JsonElement>> query = _ =>
            {
                Interlocked.Increment(ref queries);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { outputActive = false, outputReconnecting = false }));
            };
            Task Schedule() => (Task)scheduling.Invoke(subject, new object[] { new ObsOptions(), query, CancellationToken.None })!;
            CancellationTokenRegistration registration = default;
            try
            {
                if (boundary == "preparation")
                {
                    await Schedule();
                    var pending = typeof(ObsController).GetField("pendingReset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(subject)!;
                    var source = (CancellationTokenSource)pending.GetType().GetProperty("Cancellation")!.GetValue(pending)!;
                    registration = source.Token.Register(() =>
                    {
                        // Cancellation is synchronous. Invalidate the generation while
                        // preparation is still on the stack, then return period B to capture.
                        manualCancellation = subject.CancelPendingResetAsync();
                        replacement = store.ResetAsync(original).GetAwaiter().GetResult();
                        store.Record(new("rutony", "twitch", "follower", "Replacement during preparation"));
                    });
                }
                cancellingCapture = true;
                var preparing = Schedule();
                if (boundary == "capture-await")
                {
                    await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await ReplacePeriod(); releaseCapture.TrySetResult();
                }
                await preparing.WaitAsync(TimeSpan.FromSeconds(3));
                await manualCancellation.WaitAsync(TimeSpan.FromSeconds(3));
                Check(subject.Status.PendingResetUntilUtc == null, "Manual reset during " + boundary + " allowed a stale STOPPED to schedule period B");
                time.Advance(TimeSpan.FromMinutes(10));
                var state = await store.StateAsync();
                Check(state.SessionId == replacement && state.EventCount == 1 && queries == 0,
                    "Cancelled preparation queried or cleared the replacement period: " + boundary);
            }
            finally { releaseCapture.TrySetResult(); registration.Dispose(); }
        }
    }

    public static async Task QueueRaces()
    {
        await PreparationCancellation();
        foreach (var cause in new[] { "STARTING", "configure", "dispose", "manual" })
        {
            await using var fake = await ObsChecks.FakeObs.Start(); var time = new MonoTimeProvider(); var directory = Temp();
            await using var store = new CreditStore(directory); store.Record(new("rutony", "twitch", "follower", cause));
            var original = await store.StateAsync();
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var controller = Controller(directory, store, time, reset: (session, token) =>
            {
                var reset = store.ResetIfCurrentAsync(session, token); queued.TrySetResult(); return reset;
            });
            var peer = await Connect(controller, fake); await Stop(controller, fake, peer);
            var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(8)); });
            Check(entered.Wait(TimeSpan.FromSeconds(3)), "Queue blocker was not entered");
            Task? cancelling = null;
            try
            {
                time.Advance(TimeSpan.FromMinutes(5)); await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (cause == "STARTING") { await peer.Stream(cause); await fake.Barrier(controller, peer); }
                else
                {
                    cancelling = cause == "configure" ? controller.ConfigureAsync(controller.Status.Options with { Enabled = false })
                        : cause == "dispose" ? controller.DisposeAsync().AsTask() : controller.CancelPendingResetAsync();
                    await Task.Delay(60);
                    Check(!cancelling.IsCompleted, "Lifecycle acknowledged while a queued reset could still mutate state: " + cause);
                }
            }
            finally { release.Set(); }
            if (cancelling != null) await cancelling.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => controller.Status.PendingResetUntilUtc == null);
            var state = await store.StateAsync();
            Check(state.SessionId == original.SessionId && state.EventCount == 1, "Cancellation was checked only before enqueue: " + cause);
        }
        // Directly queued stale session: another reset executes first, then new data survives.
        await using var guarded = new CreditStore(Temp()); var old = (await guarded.StateAsync()).SessionId;
        var replacement = guarded.ResetAsync(old);
        guarded.Record(new("rutony", "twitch", "follower", "Replacement"));
        Check(!await guarded.ResetIfCurrentAsync(old, CancellationToken.None), "Stale session reset was accepted");
        Check((await guarded.StateAsync()).SessionId == await replacement && (await guarded.StateAsync()).EventCount == 1, "Stale queued reset damaged replacement period");
    }
}
