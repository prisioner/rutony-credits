using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using RutonyCredits;

internal static class LifecycleChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "credits-lifecycle-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyCredits");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        RuntimeHost? host = null;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            host = RuntimeHost.Start(package, directory, port);
            host.Record("twitch", "follower", "Lifecycle sample");
            var store = (CreditStore)typeof(RuntimeHost).GetField("store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
            var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
            queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(8)); });
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Store scheduling barrier not reached");
            host.Record("twitch", "subscriber", "Queued lifecycle sample");
            var closing = UiThreadCheck.Run(() => host.Dispose(), "Credits shutdown with pending store work");
            await Task.Delay(100); release.Set();
            await closing; host = null;

            await UiThreadCheck.Run(() => host = RuntimeHost.Start(package, directory, port), "Credits restart");
            using var http = new HttpClient();
            using var state = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:" + port + "/api/state"));
            if (state.RootElement.GetProperty("eventCount").GetInt32() != 2) throw new Exception("Shutdown lost a queued event");
            await UiThreadCheck.Run(() => host!.Dispose(), "Credits repeated shutdown"); host = null;
            using var reopened = RuntimeHost.Start(package, directory, port);
        }
        finally { release.Set(); host?.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
