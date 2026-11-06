namespace RutonyCredits;

// This is the small reflection boundary called by RutonyChat's standalone script.
public sealed class RuntimeHost : IDisposable
{
    private readonly CreditStore store;
    private readonly CreditsServer server;
    private readonly ObsController? obs;
    private readonly RutonyRaidSource? twitchRaids;
    private Action<string>? notification;
    private int disposed;
    private readonly int startupEventCount;
    private int startupWarningSent;
    public string ControlUrl => server.BaseUrl + "/control";
    public string OverlayUrl => server.BaseUrl + "/overlay";

    public static RuntimeHost Start(string packageDirectory, string dataDirectory, int port) =>
        Task.Run(() => new RuntimeHost(packageDirectory, dataDirectory, port)).GetAwaiter().GetResult();
    public static RuntimeHost StartWithTimeProvider(string packageDirectory, string dataDirectory, int port, TimeProvider timeProvider) =>
        Task.Run(() => new RuntimeHost(packageDirectory, dataDirectory, port, timeProvider: timeProvider)).GetAwaiter().GetResult();
    public static RuntimeHost StartPreview(string packageDirectory, string dataDirectory, int port) =>
        Task.Run(() => new RuntimeHost(packageDirectory, dataDirectory, port, "standalone")).GetAwaiter().GetResult();

    private RuntimeHost(string packageDirectory, string dataDirectory, int port, string hostMode = "rutony", TimeProvider? timeProvider = null)
    {
        store = new(dataDirectory);
        try
        {
            startupEventCount = store.StateAsync().GetAwaiter().GetResult().EventCount;
            if (hostMode == "rutony")
                twitchRaids = new RutonyRaidSource(name =>
                {
                    if (Volatile.Read(ref disposed) == 0) store.Record(new("rutony", "twitch", "raid", name));
                });
            if (hostMode == "rutony")
                obs = new ObsController(dataDirectory, SceneEnteredAsync, SceneLeftAsync,
                    () => WarnIfNonemptyAsync("При старте трансляции OBS"), CaptureSessionAsync, ResetStoppedSessionAsync, Notify, timeProvider);
            server = CreditsServer.StartAsync(store, Path.Combine(packageDirectory, "web"), port, hostMode, obs, twitchRaids).GetAwaiter().GetResult();
        }
        catch
        {
            try { if (twitchRaids != null) twitchRaids.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally
            {
                try { if (obs != null) obs.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                finally { store.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            throw;
        }
    }

    public void SetNotificationHandler(Action<string> handler)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Volatile.Write(ref notification, handler);
        if (startupEventCount > 0 && Interlocked.Exchange(ref startupWarningSent, 1) == 0)
            Notify("При включении Rutony Credits в текущем периоде уже есть события: " + startupEventCount
                + ". Проверьте статистику; при необходимости нажмите «Сбросить счётчики».");
    }

    private void Notify(string message)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { Volatile.Read(ref notification)?.Invoke("Rutony Credits: " + message + " Управление: " + ControlUrl); }
        catch { /* Notification failure must not break recording or shutdown. */ }
    }

    private async Task WarnIfNonemptyAsync(string reason)
    {
        var state = await store.StateAsync();
        if (state.EventCount > 0)
            Notify(reason + " в текущем периоде уже есть события: " + state.EventCount + ". Проверьте статистику; при необходимости нажмите «Сбросить счётчики».");
    }

    private async Task SceneEnteredAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var state = await store.StateAsync();
        if (Volatile.Read(ref disposed) == 0) await store.StartAsync(false, state.SessionId);
    }

    private async Task SceneLeftAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var state = await store.StateAsync();
        if (Volatile.Read(ref disposed) == 0 && state.RunId != null) await store.StopAsync(state.SessionId, state.RunId);
    }

    private async Task<string> CaptureSessionAsync() => (await store.StateAsync()).SessionId;

    private async Task<bool> ResetStoppedSessionAsync(string session, CancellationToken permission)
    {
        // The store stays alive until OBS has drained, even while HTTP is
        // stopping. An already due valid reset can finish during that interval.
        var reset = await store.ResetIfCurrentAsync(session, permission);
        if (reset) Notify("Трансляция OBS завершена. Счётчики сброшены; прежний журнал сохранён.");
        return reset;
    }

    public void ConfigureRutonyRaids(int port, bool secure)
    {
        if (Volatile.Read(ref disposed) != 0 || twitchRaids == null) return;
        Task.Run(() => twitchRaids.ConfigureAsync(port, secure)).GetAwaiter().GetResult();
    }
    public void Record(string site, string kind, string displayName)
    {
        var platform = site.ToLowerInvariant() switch
        { "twitch" => "twitch", "vkplay" => "vkvideolive", _ => site.ToLowerInvariant() };
        if (kind == "raid" && platform == "twitch" && twitchRaids?.OwnsTwitch == true) return;
        store.Record(new("rutony", platform, kind, displayName));
    }

    public void RecordDonation(string site, string displayName, decimal? amount, string? currency)
    {
        currency = currency?.Trim().ToUpperInvariant();
        if (amount is not >= 0 || !CreditStore.ValidCurrency(currency)) { amount = null; currency = null; }
        var platform = site.ToLowerInvariant() switch
        { "twitch" => "twitch", "vkplay" => "vkvideolive", _ => site.ToLowerInvariant() };
        store.Record(new("rutony", platform, "donation", displayName, Amount: amount, Currency: currency));
    }

    public void RecordModerator(string site, string displayName, string? userId)
    {
        var platform = site.ToLowerInvariant() switch { "twitch" => "twitch", "vkplay" => "vkvideolive", _ => "" };
        if (platform.Length == 0 || string.IsNullOrWhiteSpace(displayName)) return;
        store.Record(new("rutony", platform, "moderator", displayName, ModeratorId(displayName, userId)));
    }

    private static string ModeratorId(string displayName, string? userId)
    {
        var identity = string.IsNullOrWhiteSpace(userId) ? "name:" + displayName.Trim().ToUpperInvariant() : "id:" + userId;
        return "moderator:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    }

    public void SetChannelOwner(string site, string? displayName, string? userId)
    {
        var platform = site.ToLowerInvariant() switch { "twitch" => "twitch", "vkplay" => "vkvideolive", _ => "" };
        if (platform.Length == 0) return;
        store.ObserveChannelOwner(platform, displayName,
            string.IsNullOrWhiteSpace(userId) ? null : ModeratorId(displayName ?? "", userId));
    }

    public bool SetChannelContext(string site, string? channel)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        var platform = OwnerPlatform(site);
        return platform.Length != 0 && store.ObserveChannelContext(platform, channel);
    }

    public bool SetChannelOwnerForContext(string site, string channel, string? displayName, string? userId)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        var platform = OwnerPlatform(site);
        return platform.Length != 0 && store.ObserveChannelOwnerForContext(platform, channel, displayName,
            string.IsNullOrWhiteSpace(userId) ? null : ModeratorId(displayName ?? "", userId));
    }

    private static string OwnerPlatform(string site) => site.ToLowerInvariant() switch
    { "twitch" => "twitch", "vkplay" => "vkvideolive", _ => "" };

    // RutonyChat may call both lifecycle methods on its UI thread. Run the entire
    // async lifecycle without that context, and still wait until all resources close.
    public void Dispose() => Task.Run(async () =>
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Volatile.Write(ref notification, null);
        try
        {
            try { if (twitchRaids != null) await twitchRaids.DisposeAsync(); }
            finally { await server.DisposeAsync(); }
        }
        finally
        {
            string? session = null;
            try { if (obs != null) session = await obs.CloseAsync(); }
            finally { await store.CloseAsync(session); }
        }
    }).GetAwaiter().GetResult();
}
