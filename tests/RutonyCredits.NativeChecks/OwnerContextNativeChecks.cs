using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using RutonyChat;
using RutonyChat.ChatServices;
using RutonyCredits;

internal static class OwnerContextNativeChecks
{
    static async Task Until(Func<Task<bool>> predicate)
    { for (int i = 0; i < 350; i++) { if (await predicate()) return; await Task.Delay(10); } throw new Exception("Native context did not follow current channel"); }
    public static async Task Run(string package)
    {
        var d = Path.Combine(Path.GetTempPath(), "credits-native-context-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var api = new global::OAuth.VKPlayAPI { Result = new() { data = new() { owner = new() { id = 1, nick = "Owner A" } } } };
        var service = new FakeVkService { ServiceAPI = api, PathValue = "https://live.vkvideo.ru/a" };
        ChatServer.Services[ProgramProps.SiteEnum.vkplay] = service;
        var script = new Script();
        var parameters = new Dictionary<string,string> { ["PackageDirectory"] = package, ["DataDirectory"] = d, ["Port"] = port.ToString() };
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
        async Task<int> Count() => (await http.GetFromJsonAsync<CreditState>("/api/state", Wire.Json)).Counts.GetValueOrDefault("moderators");
        try
        {
            script.InitParams(parameters);
            ChatEventServer.Message(ProgramProps.SiteEnum.vkplay, "Owner A", true, "1");
            ChatEventServer.Message(ProgramProps.SiteEnum.vkplay, "Owner B", true, "2");
            await Until(async () => api.Requests == 1 && await Count() == 1);
            await Task.Delay(1100);
            if (api.Requests != 1) throw new Exception("Unchanged context repeated owner lookup");
            var runtime = (RuntimeHost)typeof(Script).GetField("runtime", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(script);
            var store = (CreditStore)typeof(RuntimeHost).GetField("store", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(runtime);
            var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(store);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Native admission barrier failed");
                var reads = Enumerable.Range(0, 256).Select(_ => store.StateAsync()).ToArray();
                try
                {
                    service.PathValue = "https://live.vkvideo.ru/overloaded-channel";
                    api.State = global::OAuth.OAuthBase.StatesAuth.idle;
                    ChatEventServer.Message(ProgramProps.SiteEnum.vkplay, "Regular", false, "regular");
                }
                finally { release.Set(); }
                await Task.WhenAll(reads);
            }
            if (await Count() != 2) throw new Exception("Native context overload retained foreign cache");
            service.PathValue = "https://live.vkvideo.ru/a";
            ChatEventServer.Message(ProgramProps.SiteEnum.vkplay, "Regular", false, "regular");
            if (await Count() != 1) throw new Exception("Native did not retry context after overload invalidation");
            script.Closing();
            api.State = global::OAuth.OAuthBase.StatesAuth.idle;
            script.InitParams(parameters);
            if (await Count() != 1) throw new Exception("Native failed to select same-channel persisted cache without login");
            if (api.Requests != 1) throw new Exception("Native tried login/lookup for non-ready API");
            // A channel transition is detected even with no new chat events.
            var gate = new TaskCompletionSource<global::OAuth.VKPlayAPI.ChannelInfoItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            api.Gate = gate; api.State = global::OAuth.OAuthBase.StatesAuth.ready;
            service.PathValue = "https://live.vkvideo.ru/b";
            await Until(async () => api.Requests == 2 && await Count() == 2);
            gate.SetResult(new() { data = new() { owner = new() { id = 2, nick = "Owner B" } } });
            await Until(async () => await Count() == 1);
            ChatServer.Services.Remove(ProgramProps.SiteEnum.vkplay);
            await Until(async () => await Count() == 2);
            script.Closing(); script.InitParams(parameters);
            if (await Count() != 2) throw new Exception("Unknown channel applied persisted owner");
            script.Closing();
            ChatServer.Services[ProgramProps.SiteEnum.vkplay] = service;
            var readsAfterClose = service.PathReads; var requestsAfterClose = api.Requests;
            await Task.Delay(1100);
            if (service.PathReads != readsAfterClose || api.Requests != requestsAfterClose) throw new Exception("Owner polling survived Closing");
            Console.WriteLine("PASS native channel context clears foreign cache and reuses only matching ready-independent identity");
        }
        finally { script.Closing(); ChatServer.Services.Clear(); }
    }
}
