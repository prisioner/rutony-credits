using RutonyCredits;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;

int failures = 0, checks = 0;
async Task Check(string name, Func<Task> test)
{
    checks++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); }
}
void Equal<T>(T want, T actual) { if (!EqualityComparer<T>.Default.Equals(want, actual)) throw new Exception($"Expected {want}; got {actual}"); }
string Temp() { var d = Path.Combine(Path.GetTempPath(), "rutony-credits-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

await Check("repeated names remain; only reliable source IDs deduplicate; test events excluded", async () =>
{
    await using var s = new CreditStore(Temp());
    s.Record(new("rutony", "twitch", "follower", "Orbit"));
    s.Record(new("rutony", "twitch", "follower", "Orbit"));
    s.Record(new("da", "donationalerts", "donation", "Star", "donation-1"));
    s.Record(new("da", "donationalerts", "donation", "Star", "donation-1"));
    s.Record(new("rutony", "twitch", "raid", "Test", IsTest: true));
    Equal(3, (await s.StateAsync()).EventCount);
});
await Check("chronology within sections; frozen run excludes subsequent events", async () =>
{
    await using var s = new CreditStore(Temp());
    s.Record(new("rutony", "twitch", "follower", "Later", OccurredAtUtc: DateTimeOffset.Parse("2026-01-01T12:00:00Z")));
    s.Record(new("rutony", "twitch", "follower", "Earlier", OccurredAtUtc: DateTimeOffset.Parse("2026-01-01T11:00:00Z")));
    s.Record(new("rutony", "vkvideolive", "raid", "VK guest"));
    var run = await s.StartAsync(false, (await s.StateAsync()).SessionId);
    s.Record(new("rutony", "twitch", "follower", "Next run"));
    Equal("Earlier,Later", string.Join(',', run.Sections.Single(x => x.Key == "followers").Names));
    Equal("VK guest", run.Sections[0].Names[0]);
    Equal(4, (await s.StateAsync()).EventCount);
    Equal(3, (await s.RunAsync(run.RunId))!.Sections.Sum(x => x.Names.Length));
});
await Check("reset barrier keeps old events out and preserves journal", async () =>
{
    var d = Temp(); await using var s = new CreditStore(d);
    var old = (await s.StateAsync()).SessionId;
    for (int i = 0; i < 100; i++) s.Record(new("rutony", "twitch", "follower", "Before"));
    var reset = s.ResetAsync(old);
    s.Record(new("rutony", "twitch", "follower", "After"));
    var current = await reset;
    Equal(false, current == old); Equal(1, (await s.StateAsync()).EventCount);
    Equal(100, File.ReadLines(Path.Combine(d, "sessions", old + ".jsonl")).Count());
    Equal("After", (await s.StartAsync(false, current)).Sections.Single().Names[0]);
    try { await s.ResetAsync(old); throw new Exception("Stale command accepted"); } catch (InvalidOperationException) { }
});
await Check("restart restores period and settings but never replays automatically", async () =>
{
    var d = Temp(); string id;
    await using (var s = new CreditStore(d))
    {
        id = (await s.StateAsync()).SessionId;
        s.Record(new("rutony", "twitch", "subscriber", "Comet"));
        await s.SetSettingsAsync(new() { Speed = 145, FontSize = 60 }, id);
        await s.StartAsync(false, id);
    }
    await using var restored = new CreditStore(d);
    var state = await restored.StateAsync();
    Equal(id, state.SessionId); Equal(1, state.EventCount); Equal("idle", state.Playback);
    Equal(145, state.Settings.Speed); Equal(60, state.Settings.FontSize);
});
await Check("preview does not contaminate real data; completion is run-scoped", async () =>
{
    await using var s = new CreditStore(Temp()); var id = (await s.StateAsync()).SessionId;
    var a = await s.StartAsync(true, id);
    Equal(true, a.Sections.Length > 0); Equal(0, (await s.StateAsync()).EventCount);
    var b = await s.StartAsync(true, id);
    Equal(false, await s.CompleteAsync(a.RunId, a.CompletionToken));
    Equal(false, await s.CompleteAsync(b.RunId, "wrong"));
    Equal(true, await s.CompleteAsync(b.RunId, b.CompletionToken));
    Equal("finished", (await s.StateAsync()).Playback);
});
await Check("corrupt active journal blocks real show; reset preserves corrupt bytes", async () =>
{
    var d = Temp(); string id;
    await using (var s = new CreditStore(d)) { id = (await s.StateAsync()).SessionId; }
    var file = Path.Combine(d, "sessions", id + ".jsonl");
    await File.AppendAllTextAsync(file, "{broken\n");
    await using var bad = new CreditStore(d);
    Equal(true, (await bad.StateAsync()).Error != null);
    try { await bad.StartAsync(false, id); throw new Exception("Corrupt show accepted"); } catch (InvalidOperationException) { }
    await bad.ResetAsync(id); Equal(null, (await bad.StateAsync()).Error);
    Equal("{broken\n", await File.ReadAllTextAsync(file));
});
await Check("disk failure stays visible after restart; real run cannot claim completeness", async () =>
{
    var d = Temp(); string id;
    await using (var s = new CreditStore(d))
    {
        id = (await s.StateAsync()).SessionId;
        using var held = new FileStream(Path.Combine(d, "sessions", id + ".jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        s.Record(new("rutony", "twitch", "donation", "Luna"));
        Equal(true, (await s.StateAsync()).Error != null);
        try { await s.StartAsync(false, id); throw new Exception("Incomplete show accepted"); } catch (InvalidOperationException) { }
    }
    await using var restored = new CreditStore(d);
    Equal(true, (await restored.StateAsync()).Error != null);
});
await Check("HTTP management authorization, origin, stale session, port collision and lifetime", async () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    await using var store = new CreditStore(Temp());
    var web = Temp(); await File.WriteAllTextAsync(Path.Combine(web, "control.html"), "token=__TOKEN__");
    await File.WriteAllTextAsync(Path.Combine(web, "overlay.html"), "overlay");
    await using (var server = await CreditsServer.StartAsync(store, web, port))
    {
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var id = (await store.StateAsync()).SessionId;
        Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/preview", new { sessionId = id })).StatusCode);
        client.DefaultRequestHeaders.Add("X-Credits-Token", server.ManagementToken);
        client.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
        Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/preview", new { sessionId = id })).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", server.BaseUrl);
        Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/reset", new { sessionId = "stale" })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/preview", new { sessionId = id });
        Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<CreditRun>(Wire.Json) ?? throw new Exception("No snapshot");
        Equal("preview", run.Mode);
        var stateJson = await client.GetStringAsync("/api/state");
        Equal(false, stateJson.Contains(server.ManagementToken));
        Equal(false, (await client.GetStringAsync("/api/runs/" + run.RunId)).Contains(server.ManagementToken));
        Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/stop", new StringContent("{}"))).StatusCode);
        Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/stop", new StringContent("{", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        using var badHost = new HttpRequestMessage(HttpMethod.Get, "/api/state"); badHost.Headers.Host = "evil.example:" + port;
        Equal(HttpStatusCode.Forbidden, (await client.SendAsync(badHost)).StatusCode);
        try { await using var duplicate = await CreditsServer.StartAsync(store, web, port); throw new Exception("Port collision ignored"); }
        catch (IOException) { }
    }
    // Stopping the child HTTP host must leave the collector alive.
    store.Record(new("rutony", "twitch", "follower", "Still here"));
    Equal(1, (await store.StateAsync()).EventCount);
});
await Check("late stop from a previous run cannot stop its replacement", async () =>
{
    await using var store = new CreditStore(Temp());
    var id = (await store.StateAsync()).SessionId;
    var first = await store.StartAsync(true, id);
    var second = await store.StartAsync(true, id);
    // Send the first run ID at the real HTTP boundary. Session has not changed.
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    await using var server = await CreditsServer.StartAsync(store, Temp(), port);
    using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
    client.DefaultRequestHeaders.Add("X-Credits-Token", server.ManagementToken);
    Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/stop", new {sessionId=id,runId=first.RunId})).StatusCode);
    Equal(second.RunId, (await store.StateAsync()).RunId);
    Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/stop", new {sessionId=id,runId=second.RunId})).StatusCode);
    Equal("idle", (await store.StateAsync()).Playback);
});
await Check("overflow is reported in the right period across queued resets", async () =>
{
    foreach (bool overflowBeforeReset in new[] {false, true})
    {
        await using var store = new CreditStore(Temp()); var old = (await store.StateAsync()).SessionId;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        // Only schedules the real worker; no disk/store behavior is mocked.
        // A barrier makes the overflow/reset interleaving deterministic.
        var channel = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        channel.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Worker did not reach scheduling barrier");
        Task<string> reset;
        try
        {
            if (!overflowBeforeReset) reset = store.ResetAsync(old); else reset = Task.FromResult(old);
            for (int i=0;i<10000;i++) Equal(true, store.Record(new("rutony","twitch","follower","Accepted","repeat-id")));
            Equal(false, store.Record(new("rutony","twitch","follower","Lost")));
            if (overflowBeforeReset)
            {
                reset = store.ResetAsync(old);
                Equal(false, store.Record(new("rutony","twitch","follower","Lost after reset")));
            }
        }
        finally { release.Set(); }
        var id = await reset;
        var state = await store.StateAsync(); Equal(id, state.SessionId); Equal(true, state.Error != null);
        try { await store.StartAsync(false,id); throw new Exception("Incomplete show accepted"); } catch (InvalidOperationException) { }
    }
});
await Check("missing manifest with existing journals cannot silently start an empty period", async () =>
{
    var directory = Temp(); string old;
    await using (var original = new CreditStore(directory))
    {
        old = (await original.StateAsync()).SessionId;
        original.Record(new("rutony","twitch","follower","Saved participant"));
    }
    File.Delete(Path.Combine(directory,"current.json"));
    await using var restored = new CreditStore(directory);
    var state = await restored.StateAsync(); Equal(true, state.Error != null);
    try { await restored.StartAsync(false,state.SessionId); throw new Exception("Missing manifest was ignored"); } catch (InvalidOperationException) { }
    await restored.ResetAsync(state.SessionId);
    Equal(1, File.ReadLines(Path.Combine(directory,"sessions",old+".jsonl")).Count());
    Equal(null, (await restored.StateAsync()).Error);
});
await Check("UI lifecycle releases port and store without losing pending events", LifecycleChecks.Run);
await Check("section visibility, looping snapshots, empty preview and stop", OptionsChecks.SectionsAndLoop);
await Check("donation amounts, currencies and backwards-compatible journals", OptionsChecks.DonationAmountsAndLegacy);
await Check("actual Windows font catalog over HTTP", OptionsChecks.SystemFonts);
await Check("local font face selection and safe HTTP metadata", FontFaceChecks.Run);
await Check("donation aggregation preserves chronology, source journal and frozen runs", AutomationChecks.Donations);
await Check("aggregation overflow and legacy five-section settings remain compatible", AutomationChecks.OverflowAndLegacy);
await Check("donation aggregation ignores nickname case and preserves first spelling across restart", DonationCaseChecks.Aggregation);
await Check("runtime normalizes currency codes without guessing ambiguous symbols", DonationCaseChecks.CurrencyBoundary);
await Check("unique moderators persist and reset with the session", AutomationChecks.Moderators);
await Check("OBS v5 automation, protected settings and reconnect lifecycle", ObsChecks.Run);
await Check("OBS delayed reset uses monotonic time, fresh inactive state and retains archives", ObsDelayChecks.TimingAndFreshState);
await Check("OBS reset cancels on resume, lifecycle and manual replacement without restart replay", ObsDelayChecks.CancellationAndRestart);
await Check("OBS cancellation and captured session guard run inside queued reset and lifecycle drains", ObsDelayChecks.QueueRaces);
await Check("runtime HTTP -> OBS -> snapshot/reset/warnings and missed-stop recovery", AutomationIntegrationChecks.Run);
await Check("invalid OBS settings do not disable Credits collection/HTTP and can be replaced", ObsRecoveryChecks.Run);
await Check("moderator exclusions persist without changing journals or other sections", ModeratorExclusionChecks.Exclusions);
await Check("excluded moderators are not collected, persisted or deduplicated", ModeratorCollectionChecks.Collection);
await Check("moderator collection follows queued settings changes", ModeratorCollectionChecks.QueuedSettings);
await Check("excluded legacy history preserves bytes without startup warnings", ModeratorCollectionChecks.LegacyStartup);
await Check("channel owners are excluded by nickname and stable identity", ModeratorExclusionChecks.Owners);
await Check("known owners survive restart and optional cache damage does not block credits", ModeratorExclusionChecks.OwnerCache);
await Check("runtime closing finishes only an active pending OBS reset and preserves archives", ClosingResetChecks.RuntimeClosing);
await Check("closing drains queued timer and guards replacement period without duplicate resets", ClosingResetChecks.QueuedReset);
await Check("fresh active/reconnecting/error revokes closing reset before retention warning", ClosingResetChecks.UnsafeFreshStateDuringClosing);
await Check("settings recovery preserves independent journal faults and evidence", StorageRecoveryChecks.Settings);
await Check("corrupt and oversized OBS secrets recover explicitly with rollback", StorageRecoveryChecks.ObsSecret);
await Check("bounded settings manifest and journal lines accept boundaries and reject oversize", StorageRecoveryChecks.Bounds);
await Check("commands owners HTTP overload and Close use bounded admission", AdmissionChecks.Run);
await Check("owner cache requires current channel and rejects late foreign results", OwnerContextChecks.Run);
await Check("OBS options recovery retains the valid protected password in memory", StorageRecoveryChecks.ObsOptionsKeepPassword);
await Check("owner context overload invalidates foreign cache in FIFO order", OwnerContextChecks.Overload);
await Check("large journals and maximum legal event fields remain readable", StorageRecoveryChecks.LargeJournal);
await Check("OBS commands reject contention without accumulating waiting writers", AdmissionChecks.ObsCommands);
await Check("control template reads have a conservative allocation bound", AdmissionChecks.ControlFile);
await Check("context invalidation coalescing preserves intervening admitted context barriers", OwnerContextChecks.OverloadEpoch);
await Check("Rutony live raid stream parses bounded frames without duplicate paths", RutonyRaidChecks.Flow);
await Check("Rutony raid stream reconnects and changes ports without legacy duplicates", RutonyRaidChecks.Recovery);
await Check("Rutony raid source Closing aborts an unresponsive handshake without UI", RutonyRaidChecks.Shutdown);
await Check("Rutony raid source validates ports and secure mode and aborts partial reads", RutonyRaidChecks.SecureAndPartial);
await Check("PreviewHost cannot enable the Rutony raid stream", RutonyRaidChecks.Preview);
Console.WriteLine($"Checks: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
