using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RutonyCredits;

internal static class RutonyRaidChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task Until(Func<Task<bool>> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!await predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(12)) throw new Exception("Synthetic raid source timed out"); await Task.Delay(15); }
    }
    public static string Packet(string name, string site = "Twitch") => JsonSerializer.Serialize(new { type = "Raid", data = new { name, qty = "12", site, site_img = "synthetic", tag = "Raid" } });
    public static void Configure(RuntimeHost runtime, int port, bool secure = false)
    {
        var method = typeof(RuntimeHost).GetMethod("ConfigureRutonyRaids");
        Check(method != null, "Rutony raid stream configuration boundary is missing");
        method!.Invoke(runtime, [port, secure]);
    }
    private sealed class Context : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "credits-raid-stream-" + Guid.NewGuid().ToString("N"));
        public RuntimeHost Runtime { get; }
        public HttpClient Http { get; }
        public Context(bool preview = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var package = Path.Combine(System.IO.Directory.GetCurrentDirectory(), "src", "RutonyCredits");
            Runtime = preview ? RuntimeHost.StartPreview(package, Directory, port) : RuntimeHost.Start(package, Directory, port);
            Http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
        }
        public async Task<CreditState> State() => await Http.GetFromJsonAsync<CreditState>("/api/state", Wire.Json) ?? throw new Exception("State missing");
        public async Task<bool> Connected()
        {
            using var json = JsonDocument.Parse(await Http.GetStringAsync("/api/state"));
            return json.RootElement.TryGetProperty("twitchRaids", out var source) && source.GetProperty("connected").GetBoolean();
        }
        public void Dispose() { Runtime.Dispose(); Http.Dispose(); }
    }
    public static async Task Flow()
    {
        await using var server = await FakeRutonyAlerts.Start(); using var c = new Context();
        Configure(c.Runtime, server.Port); var peer = await server.Next(); await Until(c.Connected);
        c.Runtime.Record("twitch", "raid", "Duplicate generic path"); c.Runtime.Record("vkplay", "raid", "VK Raider");
        await peer.Send(Packet("Twitch Raider"));
        var unicode = "{\"type\":\"Raid\",\"data\":{\"name\":\"Рейдер Ω\",\"qty\":\"0\",\"site\":\"Twitch\"}}";
        await peer.Send(unicode, Array.IndexOf(Encoding.UTF8.GetBytes(unicode), (byte)0xce) + 1);
        await peer.Send(Packet("Twitch Raider"));
        await Until(async () => (await c.State()).EventCount == 4);
        await peer.Send("{broken"); await peer.Send("[]"); await peer.Send("[" + Packet("Old history") + "]");
        await peer.Send("{\"type\":\"Donation\",\"data\":{\"name\":\"Other kind\",\"site\":\"Twitch\"}}");
        await peer.Send(Packet("Other site", "VKVideoLive")); await peer.Send(Packet(new string('x', 201)));
        await peer.Send(Packet("bad\nname")); await peer.Send(Packet("Binary"), binary: true);
        await peer.Send("{\"type\":\"Raid\",\"data\":{\"name\":42,\"site\":\"Twitch\"}}");
        await peer.Send("{\"type\":\"Raid\",\"padding\":\"" + new string('x', 70000) + "\",\"data\":{\"name\":\"Oversize\",\"site\":\"Twitch\"}}");
        await peer.Send(Packet("After malformed"));
        await Until(async () => (await c.State()).EventCount == 5);
        var state = await c.State(); Check(state.Counts["twitch-raids"] == 4 && state.Counts["vk-raids"] == 1, "Parser or source arbitration lost/duplicated events");
        Check(peer.ApplicationFrames == 0, "Passive client sent init/commands to Rutony");
        var html = await c.Http.GetStringAsync("/control"); c.Http.DefaultRequestHeaders.Add("X-Credits-Token", Regex.Match(html, "name=\"credits-token\" content=\"([A-F0-9]+)\"").Groups[1].Value);
        using (var response = await c.Http.PostAsJsonAsync("/api/run", new { sessionId = state.SessionId })) response.EnsureSuccessStatusCode();
        var run = await c.Http.GetFromJsonAsync<CreditRun>("/api/runs/" + (await c.State()).RunId, Wire.Json);
        Check(run!.Sections.Single(x => x.Key == "twitch-raids").Names.SequenceEqual(new[] { "Twitch Raider", "Рейдер Ω", "Twitch Raider", "After malformed" }), "Snapshot chronology/repeated real raids changed");
        c.Runtime.Dispose();
        Check(File.ReadAllLines(Path.Combine(c.Directory, "sessions", state.SessionId + ".jsonl")).Length == 5, "Shutdown lost queued raid records");
        Configure(c.Runtime, server.Port);
    }
    public static async Task Recovery()
    {
        await using var first = await FakeRutonyAlerts.Start(); await using var second = await FakeRutonyAlerts.Start(); using var c = new Context();
        c.Runtime.Record("twitch", "raid", "Legacy fallback"); Check((await c.State()).EventCount == 1, "Initial generic fallback changed");
        Configure(c.Runtime, first.Port); var peer = await first.Next(); await Until(c.Connected);
        peer.Drop(); c.Runtime.Record("twitch", "raid", "Must not fall back during reconnect");
        var reconnected = await first.Next(); await Until(c.Connected); await reconnected.Send(Packet("After reconnect"));
        await Until(async () => (await c.State()).EventCount == 2);
        Configure(c.Runtime, second.Port); var replaced = await second.Next(); await Until(c.Connected); await replaced.Send(Packet("After port change"));
        await Until(async () => (await c.State()).EventCount == 3);
        Configure(c.Runtime, 0); Check(!await c.Connected(), "Stopped source still shows connected");
        c.Runtime.Record("twitch", "raid", "Must not fall back after temporary server stop"); c.Runtime.Record("vkplay", "raid", "VK still accepted");
        Check((await c.State()).EventCount == 4, "Sticky ownership or VK collection failed");
        Configure(c.Runtime, second.Port); var resumed = await second.Next(); await Until(c.Connected); await resumed.Send(Packet("After resume"));
        await Until(async () => (await c.State()).EventCount == 5);
    }
    public static async Task SecureAndPartial()
    {
        await using var server = await FakeRutonyAlerts.Start(); using var c = new Context();
        try { Configure(c.Runtime, -1); throw new Exception("Invalid port accepted"); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException) { }
        Configure(c.Runtime, server.Port, true);
        await Until(async () =>
        {
            using var json = JsonDocument.Parse(await c.Http.GetStringAsync("/api/state"));
            var status = json.RootElement.GetProperty("twitchRaids");
            return !status.GetProperty("connected").GetBoolean() && status.GetProperty("error").ValueKind == JsonValueKind.String;
        });
        Configure(c.Runtime, server.Port, false); var peer = await server.Next(); await Until(c.Connected);
        await peer.Send(Packet("After secure mismatch")); await Until(async () => (await c.State()).EventCount == 1);
        await peer.Partial("{\"type\":\"Raid\",\"data\":{");
        var timer = Stopwatch.StartNew(); c.Runtime.Dispose();
        Check(timer.Elapsed < TimeSpan.FromSeconds(3), "Closing waited for a partial message to finish");
    }
    public static async Task Preview()
    {
        await using var server = await FakeRutonyAlerts.Start(); using var c = new Context(preview: true);
        Configure(c.Runtime, server.Port);
        using var json = JsonDocument.Parse(await c.Http.GetStringAsync("/api/state"));
        Check(!json.RootElement.GetProperty("twitchRaids").GetProperty("available").GetBoolean(), "Preview exposes an active Rutony raid source");
        Check(server.Handshakes == 0, "Preview connected to Rutony's overlay stream");
    }
    private sealed class NoPump : SynchronizationContext { public override void Post(SendOrPostCallback callback, object? state) => throw new Exception("UI dispatch is forbidden"); }
    public static async Task Shutdown()
    {
        await using var held = await FakeRutonyAlerts.Start(holdHandshake: true); using var c = new Context();
        Configure(c.Runtime, held.Port); await Until(() => Task.FromResult(held.Handshakes > 0));
        var timer = Stopwatch.StartNew(); var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NoPump());
        try { c.Runtime.Dispose(); c.Runtime.Dispose(); Configure(c.Runtime, held.Port); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Check(timer.Elapsed < TimeSpan.FromSeconds(3), "Closing waited for an unresponsive handshake");
    }
}