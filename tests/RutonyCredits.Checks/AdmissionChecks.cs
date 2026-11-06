using System.Reflection;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using RutonyCredits;

internal static class AdmissionChecks
{
    public static async Task ControlFile()
    {
        var d = Path.Combine(Path.GetTempPath(), "credits-control-bound-" + Guid.NewGuid().ToString("N"));
        await using var store = new CreditStore(d);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        File.WriteAllText(Path.Combine(d, "control.html"), new string('x', 1024 * 1024));
        await using var server = await CreditsServer.StartAsync(store, d, port);
        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        if ((await http.GetAsync("/control")).StatusCode != HttpStatusCode.OK) throw new Exception("Boundary control template rejected");
        File.AppendAllText(Path.Combine(d, "control.html"), "x");
        if ((await http.GetAsync("/control")).StatusCode != HttpStatusCode.ServiceUnavailable) throw new Exception("Oversized control template read without a bound");
    }
    public static async Task ObsCommands()
    {
        var d = Path.Combine(Path.GetTempPath(), "credits-obs-admission-" + Guid.NewGuid().ToString("N"));
        await using var obs = new ObsController(d, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
        var gate = (SemaphoreSlim)typeof(ObsController).GetField("lifecycle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(obs)!;
        await gate.WaitAsync();
        var configure = obs.ConfigureAsync(new()); var cancel = obs.CancelPendingResetAsync();
        try
        {
            if (!configure.IsFaulted || !cancel.IsFaulted) throw new Exception("OBS command waits accumulate outside bounded admission");
        }
        finally
        {
            gate.Release();
            try { await configure; } catch (StoreOverloadedException) { }
            try { await cancel; } catch (StoreOverloadedException) { }
        }
        await obs.ConfigureAsync(new()); // admission recovers after the active operation
    }
    public static async Task Run()
    {
        var d = Path.Combine(Path.GetTempPath(), "credits-admission-" + Guid.NewGuid().ToString("N"));
        await using var store = new CreditStore(d);
        var id = (await store.StateAsync()).SessionId;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var channel = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        channel.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(20)); });
        if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Worker did not block");
        var commands = new List<Task>();
        Task closing = Task.CompletedTask;
        try
        {
            // Owners and reads consume the same finite command budget.
            for (var i = 0; i < 128; i++)
                if (!store.ObserveChannelOwner("twitch", "Owner", null)) throw new Exception("Premature owner overload");
            for (var i = 0; i < 128; i++) commands.Add(store.StateAsync());
            var rejected = store.StateAsync();
            if (!rejected.IsFaulted) throw new Exception("Command admission left an unbounded pending reader");
            try { await rejected; } catch (InvalidOperationException) { }
            if (store.ObserveChannelOwner("twitch", "Other", null)) throw new Exception("Owner flood bypassed command limit");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            await using (var server = await CreditsServer.StartAsync(store, d, port))
            {
                using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromSeconds(3) };
                if ((await http.GetAsync("/api/state")).StatusCode != HttpStatusCode.ServiceUnavailable) throw new Exception("HTTP overload not explicit");
            }
            // Accepted records still drain on Close even when all command slots are occupied.
            if (!store.Record(new("rutony", "twitch", "follower", "Accepted during read flood"))) throw new Exception("Record reserve lost");
            closing = store.DisposeAsync().AsTask();
            if (closing.IsCompleted) throw new Exception("Close did not drain");
        }
        finally { release.Set(); }
        await Task.WhenAll(commands); await closing;
        await using var restored = new CreditStore(d);
        if ((await restored.StateAsync()).EventCount != 1 || (await restored.StateAsync()).Error != null) throw new Exception("Admission or Close lost accepted record");
    }
}
