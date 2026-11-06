using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using RutonyChat;
using RutonyChat.ChatServices;
using RutonyCredits;

internal static class InvalidContextNativeChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Until(Func<Task<bool>> predicate)
    { for (int i = 0; i < 200; i++) { if (await predicate()) return; await Task.Delay(10); } throw new Exception("Invalid-context observation timed out"); }

    public static async Task Run(string package)
    {
        foreach (var site in new[] { ProgramProps.SiteEnum.twitch, ProgramProps.SiteEnum.vkplay })
        foreach (bool saturated in new[] { false, true })
        {
            var d = Path.Combine(Path.GetTempPath(), "credits-invalid-context-" + Guid.NewGuid().ToString("N"));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var root = site == ProgramProps.SiteEnum.twitch ? "https://www.twitch.tv/" : "https://live.vkvideo.ru/";
            var api = new global::OAuth.VKPlayAPI { Result = new() { data = new() { owner = new() { id = 1, nick = "oldowner" } } } };
            ChatService service = site == ProgramProps.SiteEnum.twitch ? new ChatService() : new FakeVkService { ServiceAPI = api };
            service.PathValue = root + "oldowner";
            ChatServer.Services[site] = service;
            var script = new Script();
            try
            {
                script.InitParams(new() { ["PackageDirectory"] = package, ["DataDirectory"] = d, ["Port"] = port.ToString() });
                var runtime = (RuntimeHost)typeof(Script).GetField("runtime", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(script);
                var store = (CreditStore)typeof(RuntimeHost).GetField("store", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(runtime);
                var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(store);
                ChatEventServer.Message(site, "oldowner", true, "1");
                ChatEventServer.Message(site, "newowner", true, "2");
                await Until(async () => (await store.StateAsync()).Counts.GetValueOrDefault("moderators") == 1);
                var initial = await store.StateAsync();
                var invalid = site == ProgramProps.SiteEnum.twitch
                    ? new[] { "%0A", "%20%20", "%E2%80%83", "old%00owner", new string('x', 2049) }
                    : new[] { new string('x', 2049) };
                foreach (var component in invalid)
                {
                    // Reestablish A before each malformed input: every invalid category
                    // must clear a live owner, not merely preserve an already-unknown state.
                    service.PathValue = root + "oldowner";
                    ChatEventServer.Message(site, "Regular", false, "regular");
                    await Until(async () => (await store.StateAsync()).Counts["moderators"] == 1);
                    if (saturated)
                    {
                        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                        queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
                        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Invalid-context worker barrier failed");
                        var reads = Enumerable.Range(0, 256).Select(_ => store.StateAsync()).ToArray();
                        try
                        {
                            service.PathValue = root + component;
                            for (int i = 0; i < 2000; i++) ChatEventServer.Message(site, "Regular", false, "regular");
                            Check(queue.Reader.Count == 257, "Invalid context did not produce one bounded safety marker");
                        }
                        finally { release.Set(); }
                        await Task.WhenAll(reads);
                        Check(reads.All(t => t.Result.Counts["moderators"] == 1), "Invalidation reordered prior accepted reads");
                    }
                    else
                    {
                        service.PathValue = root + component;
                        ChatEventServer.Message(site, "Regular", false, "regular");
                    }
                    var state = await store.StateAsync();
                    Check(state.Counts["moderators"] == 2, "Malformed native context kept old owner active: " + site + ", length=" + component.Length);
                    Check(state.SessionId == initial.SessionId && state.EventCount == 2 && state.Error == null, "Context recovery changed the period or events");
                    Check((await store.StartAsync(false, state.SessionId)).Sections.Single().Names.SequenceEqual(new[] { "oldowner", "newowner" }), "Real run retained the old owner exclusion");
                }
                // Unknown -> valid B must retry normally, including after saturated rejection.
                api.Result = new() { data = new() { owner = new() { id = 2, nick = "newowner" } } };
                service.PathValue = root + "newowner";
                ChatEventServer.Message(site, "Regular", false, "regular");
                await Until(async () => (await store.StartAsync(false, initial.SessionId)).Sections.Single().Names.SequenceEqual(new[] { "oldowner" }));
                Check((await store.StateAsync()).EventCount == 2, "Valid retry changed source events");
            }
            finally { script.Closing(); ChatServer.Services.Clear(); }
        }
        Console.WriteLine("PASS native malformed/whitespace/control/oversized contexts become unknown with ordered bounded overload and valid retry");
    }
}
