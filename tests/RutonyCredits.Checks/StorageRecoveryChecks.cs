using RutonyCredits;
using System.Text.Json;

internal static class StorageRecoveryChecks
{
    static string Temp() { var d = Path.Combine(Path.GetTempPath(), "credits-storage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task Settings()
    {
        var d = Temp(); string id;
        await using (var s = new CreditStore(d)) { id = (await s.StateAsync()).SessionId; s.Record(new("rutony", "twitch", "follower", "Before")); }
        File.WriteAllText(Path.Combine(d, "settings.json"), "{broken");
        await using (var s = new CreditStore(d))
        {
            Check((await s.StateAsync()).Error != null, "Missing settings fault");
            s.Record(new("rutony", "twitch", "follower", "During settings fault"));
            Check((await s.StateAsync()).EventCount == 2, "Settings fault blocked collection");
            await s.SetSettingsAsync(new() { Speed = 123 }, id);
            Check((await s.StateAsync()).Error == null, "Save did not clear settings fault");
            Check(Directory.GetFiles(d, "settings.json.recovery-*").Any(p => File.ReadAllText(p) == "{broken"), "Corrupt settings bytes lost");
            await s.StartAsync(false, id);
        }
        await using (var s = new CreditStore(d)) Check((await s.StateAsync()).Settings.Speed == 123 && (await s.StateAsync()).Error == null, "Save recovery did not survive restart");
        File.WriteAllText(Path.Combine(d, "settings.json"), "null");
        await using (var s = new CreditStore(d))
        {
            id = await s.ResetAsync(id);
            Check((await s.StateAsync()).Error != null, "Reset hid settings fault");
        }
        File.AppendAllText(Path.Combine(d, "sessions", id + ".jsonl"), "{bad\n");
        await using (var s = new CreditStore(d))
        {
            await s.SetSettingsAsync(new(), id);
            Check((await s.StateAsync()).Error != null, "Save hid journal fault");
        }
    }
    public static async Task Bounds()
    {
        foreach (var extra in new[] { 0, 1 })
        {
            var d = Temp(); string id;
            await using (var s = new CreditStore(d)) id = (await s.StateAsync()).SessionId;
            var json = JsonSerializer.Serialize(new CreditSettings { ModeratorExclusions = Enumerable.Range(0, 50).Select(i => i.ToString("D2") + new string('Ж', 198)).ToArray() }, Wire.Json);
            File.WriteAllText(Path.Combine(d, "settings.json"), json.PadRight(262144 + extra));
            await using var loaded = new CreditStore(d);
            Check(((await loaded.StateAsync()).Error != null) == (extra != 0), "Settings byte bound not enforced at boundary");
        }
        foreach (var extra in new[] { 0, 1 })
        {
            var d = Temp(); string id;
            await using (var s = new CreditStore(d)) id = (await s.StateAsync()).SessionId;
            var input = new CreditEvent(id, Guid.NewGuid().ToString("N"), 1, "rutony", "twitch", "follower", "Boundary", DateTimeOffset.UtcNow, null, null);
            File.WriteAllText(Path.Combine(d, "sessions", id + ".jsonl"), JsonSerializer.Serialize(input, Wire.Json).PadRight(16384 + extra) + "\n");
            await using var loaded = new CreditStore(d);
            Check(((await loaded.StateAsync()).Error != null) == (extra != 0), "JSONL line bound not enforced at boundary");
        }
        var manifest = Temp();
        await using (var s = new CreditStore(manifest)) { }
        File.AppendAllText(Path.Combine(manifest, "current.json"), new string(' ', 4096));
        await using var oversized = new CreditStore(manifest);
        Check((await oversized.StateAsync()).Error != null, "Oversized manifest accepted");
        var manifestBytes = File.ReadAllBytes(Path.Combine(manifest, "current.json"));
        await oversized.ResetAsync((await oversized.StateAsync()).SessionId);
        Check(Directory.GetFiles(manifest, "current.json.recovery-*").Any(p => File.ReadAllBytes(p).SequenceEqual(manifestBytes)), "Reset lost malformed manifest evidence");
    }
    public static async Task LargeJournal()
    {
        var d = Temp(); string id;
        await using (var s = new CreditStore(d)) id = (await s.StateAsync()).SessionId;
        using (var writer = new StreamWriter(Path.Combine(d, "sessions", id + ".jsonl")))
            for (int i = 1; i <= 12000; i++)
            {
                var e = new CreditEvent(id, Guid.NewGuid().ToString("N"), i, new string('Ж', 80), new string('Ж', 80), "donation",
                    new string('Ж', 200), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, i.ToString() + new string('"', 190), decimal.MaxValue, "ABCDEFGHIJKL");
                writer.WriteLine(JsonSerializer.Serialize(e, Wire.Json));
            }
        await using var loaded = new CreditStore(d);
        var state = await loaded.StateAsync();
        Check(state.Error == null && state.EventCount == 12000, "Reader imposed a whole-journal/event-count cap or rejected maximum legal fields");
    }
    public static async Task ObsOptionsKeepPassword()
    {
        var d = Temp();
        await using (var first = new ObsController(d, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
            await first.ConfigureAsync(new(), "synthetic-valid-password");
        var cipher = File.ReadAllBytes(Path.Combine(d, "obs-secret.bin"));
        File.WriteAllText(Path.Combine(d, "obs.json"), "null");
        await using var recovery = new ObsController(d, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
        await recovery.ConfigureAsync(new());
        Check(recovery.Status.HasPassword && recovery.Status.Error == null, "Options recovery forgot the retained password in memory");
        Check(File.ReadAllBytes(Path.Combine(d, "obs-secret.bin")).SequenceEqual(cipher), "Options recovery changed protected password");
    }
    public static async Task ObsSecret()
    {
        foreach (var size in new[] { 20, 65537 })
        foreach (var clear in new[] { false, true })
        {
            var d = Temp(); var original = Enumerable.Repeat((byte)0x71, size).ToArray();
            File.WriteAllBytes(Path.Combine(d, "obs-secret.bin"), original);
            await using (var obs = new ObsController(d, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { }))
            {
                Check(obs.Status.Error != null, "Corrupt secret accepted");
                try { await obs.ConfigureAsync(new()); throw new Exception("Save silently hid corrupt secret"); }
                catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException) { }
                await obs.ConfigureAsync(new(), clear ? null : "synthetic-password", clear);
                Check(obs.Status.Error == null, "Explicit secret recovery failed");
                Check(Directory.GetFiles(d, "obs-secret.bin.recovery-*").Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "Secret evidence lost");
            }
            await using var restored = new ObsController(d, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
            Check(restored.Status.Error == null && restored.Status.HasPassword == !clear, "Secret recovery not durable");
        }
        var rollback = Temp(); var bytes = new byte[65537]; File.WriteAllBytes(Path.Combine(rollback, "obs-secret.bin"), bytes);
        Directory.CreateDirectory(Path.Combine(rollback, "obs.json")); // deterministic failed replace after secret write
        await using var bad = new ObsController(rollback, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
        try { await bad.ConfigureAsync(new(), "new-password"); throw new Exception("Expected failed options write"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Check(File.ReadAllBytes(Path.Combine(rollback, "obs-secret.bin")).SequenceEqual(bytes), "Failed options write changed secret");
    }
}
