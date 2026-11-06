using RutonyCredits;
using System.Text.Json;
using System.Reflection;
using System.Threading.Channels;

internal static class OwnerContextChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static bool Call(CreditStore store, string name, params object?[] args) =>
        (bool)(typeof(CreditStore).GetMethod(name)?.Invoke(store, args) ?? throw new Exception("Missing contextual owner boundary: " + name));
    public static async Task Overload()
    {
        var d = Path.Combine(Path.GetTempPath(), "credits-context-load-" + Guid.NewGuid().ToString("N"));
        await using var s = new CreditStore(d);
        s.ObserveChannelContext("twitch", "a"); s.ObserveChannelOwnerForContext("twitch", "a", "Old", "1");
        s.Record(new("rutony", "twitch", "moderator", "Old", "1"));
        Check((await s.StateAsync()).Counts["moderators"] == 0, "Setup owner not filtered");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
        queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Worker barrier failed");
        var reads = new List<Task<CreditState>>();
        try
        {
            for (int i = 0; i < 256; i++) reads.Add(s.StateAsync());
            for (int i = 0; i < 2000; i++) Check(!s.ObserveChannelContext("twitch", "b"), "Context overload not explicit");
            Check(queue.Reader.Count == 257, "Repeated safety invalidations grew the pending queue");
        }
        finally { release.Set(); }
        await Task.WhenAll(reads);
        Check(reads.All(t => t.Result.Counts["moderators"] == 0), "Safety invalidation reordered earlier accepted reads");
        Check((await s.StateAsync()).Counts["moderators"] == 1, "Rejected context change left foreign owner active");
        s.ObserveChannelOwnerForContext("twitch", "a", "Old", "1");
        Check((await s.StateAsync()).Counts["moderators"] == 1, "Late owner result revived context after overload");
        Check(s.ObserveChannelContext("twitch", "b"), "Context retry rejected after drain");
    }
    public static async Task OverloadEpoch()
    {
        var d = Path.Combine(Path.GetTempPath(), "credits-context-epoch-" + Guid.NewGuid().ToString("N"));
        await using var s = new CreditStore(d);
        s.ObserveChannelContext("twitch", "a"); s.ObserveChannelOwnerForContext("twitch", "a", "Owner", "id");
        s.Record(new("rutony", "twitch", "moderator", "Owner", "id"));
        await s.StateAsync();
        var queue = (Channel<Action>)typeof(CreditStore).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
        using var firstEntered = new ManualResetEventSlim(); using var firstRelease = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim(); using var secondRelease = new ManualResetEventSlim();
        queue.Writer.TryWrite(() => { firstEntered.Set(); firstRelease.Wait(TimeSpan.FromSeconds(10)); });
        Check(firstEntered.Wait(TimeSpan.FromSeconds(5)), "First barrier failed");
        var reads = new List<Task<CreditState>> { s.StateAsync() };
        queue.Writer.TryWrite(() => { secondEntered.Set(); secondRelease.Wait(TimeSpan.FromSeconds(10)); });
        try
        {
            for (int i = 1; i < 256; i++) reads.Add(s.StateAsync());
            Check(!s.ObserveChannelContext("twitch", "b"), "First overload missing"); // marker before accepted B
            firstRelease.Set();
            Check(secondEntered.Wait(TimeSpan.FromSeconds(5)), "Second barrier failed");
            Check(s.ObserveChannelContext("twitch", "b"), "Freed command slot not reusable");
            Check(!s.ObserveChannelContext("twitch", "c"), "Second overload missing"); // marker after accepted B
        }
        finally { firstRelease.Set(); secondRelease.Set(); }
        await Task.WhenAll(reads);
        s.ObserveChannelOwnerForContext("twitch", "b", "Owner", "id");
        Check((await s.StateAsync()).Counts["moderators"] == 1, "Invalidations coalesced across an accepted context barrier");
    }
    public static async Task Run()
    {
        var d = Path.Combine(Path.GetTempPath(), "credits-context-" + Guid.NewGuid().ToString("N")); string id;
        await using (var s = new CreditStore(d))
        {
            id = (await s.StateAsync()).SessionId;
            s.Record(new("rutony", "twitch", "moderator", "Old owner", "owner-a"));
            s.Record(new("rutony", "twitch", "moderator", "Other", "other"));
        }
        var cache = Path.Combine(d, "sessions", id + ".owners.json");
        File.WriteAllText(cache, """{"twitch":{"name":"Old owner","eventId":"owner-a"}}""");
        await using (var s = new CreditStore(d))
        {
            Check((await s.StateAsync()).Counts["moderators"] == 2, "Unscoped legacy cache applied to unknown channel");
            Check(Call(s, "ObserveChannelContext", "twitch", "channel-a"), "Context rejected");
            Check(Call(s, "ObserveChannelOwnerForContext", "twitch", "channel-a", "Old owner", "owner-a"), "Owner rejected");
            Check((await s.StateAsync()).Counts["moderators"] == 1, "Matching current owner not applied");
        }
        await using (var s = new CreditStore(d))
        {
            Check((await s.StateAsync()).Counts["moderators"] == 2, "Restart applied cache before current context");
            Call(s, "ObserveChannelContext", "twitch", "channel-a");
            Check((await s.StateAsync()).Counts["moderators"] == 1, "Matching cache failed to restore without lookup");
            Call(s, "ObserveChannelContext", "twitch", "channel-b");
            Check((await s.StateAsync()).Counts["moderators"] == 2, "New channel inherited owner cache");
            Call(s, "ObserveChannelOwnerForContext", "twitch", "channel-a", "Other", "other");
            Check((await s.StateAsync()).Counts["moderators"] == 2, "Late old-context result affected new channel");
            Call(s, "ObserveChannelOwnerForContext", "twitch", "channel-b", "Other", "other");
            Check((await s.StartAsync(false, id)).Sections.Single().Names.Single() == "Old owner", "Current owner not selected");
            Call(s, "ObserveChannelContext", "twitch", null);
            Check((await s.StateAsync()).Counts["moderators"] == 2, "Unknown channel retained owner");
            id = await s.ResetAsync(id);
            s.Record(new("rutony", "twitch", "moderator", "Other", "other"));
            Call(s, "ObserveChannelContext", "twitch", "channel-b");
            Check((await s.StateAsync()).Counts["moderators"] == 0, "Reset lost contextual cache");
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(d, "sessions", id + ".owners.json")));
        Check(doc.RootElement.GetProperty("twitch").GetProperty("context").GetString() == "channel-b", "Context not persisted with owner");
    }
}
