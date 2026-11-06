using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using RutonyCredits;
using RutonyChat;

internal static class RaidNativeChecks
{
    private static async Task Until(Func<Task<bool>> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!await predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Native raid stream did not become ready"); await Task.Delay(20); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class NoPump : SynchronizationContext { public override void Post(SendOrPostCallback callback, object state) => throw new Exception("UI posting is forbidden"); }
    public static async Task Run(string package)
    {
        await using var first = await FakeRutonyAlerts.Start(); await using var second = await FakeRutonyAlerts.Start();
        var directory = Path.Combine(Path.GetTempPath(), "credits-native-raids-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
        var script = new Script();
        var parameters = new Dictionary<string, string> { ["PackageDirectory"] = package, ["DataDirectory"] = directory, ["Port"] = port.ToString(CultureInfo.InvariantCulture) };
        async Task<CreditState> State() => await http.GetFromJsonAsync<CreditState>("/api/state", Wire.Json) ?? throw new Exception("State missing");
        string Packet(string name) => JsonSerializer.Serialize(new { type = "Raid", data = new { name, qty = "12", site = "Twitch", tag = "Raid" } });
        var oldServer = ChatLocalServer._HttpServer; var oldActive = ChatLocalServer.httpServerActive;
        try
        {
            ChatLocalServer.httpServerActive = true; ChatLocalServer._HttpServer = new FakeRutonyHttpServer { Port = first.Port };
            script.InitParams(parameters); var peer = await first.Next(); await Until(async () => (await State()).TwitchRaids.Connected);
            ChatEventServer.Raid(ProgramProps.SiteEnum.twitch, "Same native Twitch event", 12);
            ChatEventServer.Raid(ProgramProps.SiteEnum.vkplay, "VK event", 3);
            await peer.Send(Packet("Same native Twitch event"));
            await Until(async () => (await State()).EventCount == 2);
            var state = await State(); Check(state.Counts["twitch-raids"] == 1 && state.Counts["vk-raids"] == 1, "Native public discovery or duplicate arbitration failed");
            ChatLocalServer._HttpServer = new FakeRutonyHttpServer { Port = second.Port };
            var changed = await second.Next(); await Until(async () => (await State()).TwitchRaids.Connected);
            await changed.Send(Packet("After server replacement")); await Until(async () => (await State()).EventCount == 3);
            Check(peer.ApplicationFrames == 0 && changed.ApplicationFrames == 0, "Native source sent init to overlay stream");
            var previous = SynchronizationContext.Current; var timer = Stopwatch.StartNew(); SynchronizationContext.SetSynchronizationContext(new NoPump());
            try { script.Closing(); script.Closing(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Check(timer.Elapsed < TimeSpan.FromSeconds(3), "Native Closing blocked on raid source");
            ChatEventServer.Raid(ProgramProps.SiteEnum.twitch, "After Closing", 1);
            script.InitParams(parameters); var restarted = await second.Next(); await Until(async () => (await State()).TwitchRaids.Connected);
            Check((await State()).EventCount == 3, "Restart lost history or Closing left a subscription");
            ChatEventServer.Raid(ProgramProps.SiteEnum.twitch, "Only once", 12); await restarted.Send(Packet("Only once"));
            await Until(async () => (await State()).EventCount == 4);
            script.Closing(); Check(File.ReadAllLines(Path.Combine(directory, "sessions", state.SessionId + ".jsonl")).Length == 4, "Native reinit duplicated a raid");
            Console.WriteLine("PASS native public Rutony server discovery, live Twitch stream, VK, single ingress, replacement and UI Closing/reinit");
        }
        finally { script.Closing(); ChatLocalServer._HttpServer = oldServer; ChatLocalServer.httpServerActive = oldActive; }
    }
}