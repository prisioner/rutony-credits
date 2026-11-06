using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using RutonyCredits;

internal static class ModeratorCollectionChecks
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "credits-moderator-collection-" + Guid.NewGuid().ToString("N"));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Journal(string directory, string id) => Path.Combine(directory, "sessions", id + ".jsonl");

    public static async Task Collection()
    {
        var directory = Temp(); string id;
        await using (var store = new CreditStore(directory))
        {
            var state = await store.StateAsync(); id = state.SessionId;
            await store.SetSettingsAsync(state.Settings with { ModeratorExclusions = [" @Build Bot ", "Тестовый бот"] }, id);
            store.Record(new("rutony", "twitch", "moderator", " @BUILD BOT ", "bot-id"));
            store.Record(new("rutony", "vkvideolive", "moderator", "тестовый БОТ", "vk-bot-id"));
            state = await store.StateAsync();
            Check(state.EventCount == 0 && state.Counts.Values.All(x => x == 0), "Excluded chat moderators created hidden events");
            Check(new FileInfo(Journal(directory, id)).Length == 0, "Excluded moderators were written to disk");
            Check((await store.StartAsync(false, id)).Sections.Length == 0, "Excluded moderators created a credit section");
            store.Record(new("rutony", "twitch", "follower", "Build Bot"));
            store.Record(new("rutony", "twitch", "donation", "Build Bot", Amount: 2, Currency: "RUB"));
            store.Record(new("rutony", "twitch", "moderator", "Helper", "helper-id"));
            state = await store.StateAsync();
            Check(state.EventCount == 3 && state.Counts["moderators"] == 1 && state.Counts["followers"] == 1 && state.Counts["donations"] == 1,
                "Moderator exclusions suppressed allowed moderators or other event kinds");
        }
        await using (var store = new CreditStore(directory))
        {
            var state = await store.StateAsync();
            store.Record(new("rutony", "twitch", "moderator", "build bot", "another-bot-id"));
            Check((await store.StateAsync()).EventCount == 3, "Restored exclusions did not prevent new collection");
            await store.SetSettingsAsync(state.Settings with { ModeratorExclusions = [] }, id);
            store.Record(new("rutony", "twitch", "moderator", "Build Bot", "bot-id"));
            state = await store.StateAsync();
            Check(state.EventCount == 4 && state.Counts["moderators"] == 2, "Removing exclusion failed to collect a previously ignored identity");
            var saved = File.ReadAllLines(Journal(directory, id)).Select(line => JsonSerializer.Deserialize<CreditEvent>(line, Wire.Json)!).ToArray();
            Check(saved.Length == 4 && saved.Select(x => x.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4 }), "Ignored events consumed journal sequence or dedup IDs");
        }
    }

    public static async Task QueuedSettings()
    {
        var directory = Temp();
        await using var store = new CreditStore(directory);
        var state = await store.StateAsync(); var id = state.SessionId;
        var excluded = state.Settings with { ModeratorExclusions = ["Bot"] };
        var firstSave = store.SetSettingsAsync(excluded, id);
        store.Record(new("rutony", "twitch", "moderator", "Bot", "first-id"));
        var remove = store.SetSettingsAsync(state.Settings, id);
        store.Record(new("rutony", "twitch", "moderator", "Bot", "first-id"));
        var secondSave = store.SetSettingsAsync(excluded, id);
        store.Record(new("rutony", "twitch", "moderator", "Bot", "second-id"));
        await Task.WhenAll(firstSave, remove, secondSave);
        Check((await store.StateAsync()).EventCount == 0, "Hidden historical moderator remained in the total");
        Check(File.ReadAllLines(Journal(directory, id)).Length == 1, "Exclusions did not respect queued settings barriers");
        await store.SetSettingsAsync(state.Settings, id);
        store.Record(new("rutony", "twitch", "moderator", "Bot", "first-id"));
        store.Record(new("rutony", "twitch", "moderator", "Bot", "second-id"));
        Check((await store.StateAsync()).EventCount == 2 && File.ReadAllLines(Journal(directory, id)).Length == 2,
            "Excluded records polluted dedup or allowed duplicate collection after settings changes");
    }

    public static async Task LegacyStartup()
    {
        var directory = Temp(); string id;
        await using (var store = new CreditStore(directory))
        {
            var state = await store.StateAsync(); id = state.SessionId;
            store.Record(new("rutony", "twitch", "moderator", "Legacy Bot", "old-id"));
            Check((await store.StateAsync()).EventCount == 1, "Legacy fixture was not recorded");
            await store.SetSettingsAsync(state.Settings with { ModeratorExclusions = ["legacy bot"] }, id);
            Check((await store.StateAsync()).EventCount == 0, "Old excluded moderator still contributes to total events");
        }
        var original = File.ReadAllBytes(Journal(directory, id));
        var package = Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyCredits");
        int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
        using (var runtime = RuntimeHost.Start(package, directory, Port()))
        {
            var messages = new ConcurrentQueue<string>(); runtime.SetNotificationHandler(messages.Enqueue);
            Check(messages.IsEmpty, "Excluded-only legacy journal triggered a startup warning");
            runtime.RecordModerator("twitch", "LEGACY BOT", "new-id");
            using var http = new HttpClient();
            var state = await http.GetFromJsonAsync<CreditState>(new Uri(new Uri(runtime.ControlUrl), "/api/state"), Wire.Json);
            Check(state?.EventCount == 0 && state.Counts.GetValueOrDefault("moderators") == 0, "Runtime collection or HTTP total includes excluded moderator");
        }
        Check(File.ReadAllBytes(Journal(directory, id)).SequenceEqual(original), "Legacy journal was rewritten or new excluded events were appended");
        await using (var store = new CreditStore(directory))
        {
            var state = await store.StateAsync();
            await store.SetSettingsAsync(state.Settings with { ModeratorExclusions = [] }, id);
            Check((await store.StateAsync()).EventCount == 1, "Removing exclusion lost the original historical record");
            await store.SetSettingsAsync(state.Settings, id);
            store.Record(new("rutony", "twitch", "follower", "Follower"));
            Check((await store.StateAsync()).EventCount == 1, "Unrelated follower was hidden with legacy moderator");
        }
        using (var runtime = RuntimeHost.Start(package, directory, Port()))
        {
            var messages = new ConcurrentQueue<string>(); runtime.SetNotificationHandler(messages.Enqueue);
            Check(messages.Count == 1 && messages.Single().Contains("события: 1."), "Legitimate nonempty-session startup warning was suppressed or included hidden history");
        }
    }
}