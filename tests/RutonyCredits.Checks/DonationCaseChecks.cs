using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using RutonyCredits;

internal static class DonationCaseChecks
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "credits-donation-case-" + Guid.NewGuid().ToString("N"));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Aggregation()
    {
        var directory = Temp(); var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            await using (var store = new CreditStore(directory))
            {
                var state = await store.StateAsync(); var id = state.SessionId;
                var t = DateTimeOffset.Parse("2026-01-01T12:00:00Z", CultureInfo.InvariantCulture);
                store.Record(new("oda", "other", "donation", "MiXeD", OccurredAtUtc: t, Amount: 1.10m, Currency: "usd"));
                store.Record(new("da", "donationalerts", "donation", "mixed", OccurredAtUtc: t.AddSeconds(2), Amount: 2.20m, Currency: "USD"));
                store.Record(new("oda", "other", "donation", "MIXED", OccurredAtUtc: t.AddSeconds(3), Amount: 3.30m, Currency: "Usd"));
                store.Record(new("oda", "other", "donation", "Next", OccurredAtUtc: t.AddSeconds(1), Amount: 4m, Currency: "USD"));
                store.Record(new("oda", "other", "donation", "mixed", OccurredAtUtc: t.AddSeconds(4), Amount: 5m, Currency: "EUR"));
                store.Record(new("oda", "other", "donation", "Ёж", OccurredAtUtc: t.AddSeconds(5), Amount: 10m, Currency: "RUB"));
                store.Record(new("da", "other", "donation", "ёЖ", OccurredAtUtc: t.AddSeconds(6), Amount: 20m, Currency: "rub"));
                store.Record(new("oda", "other", "donation", "MiXeD", OccurredAtUtc: t.AddSeconds(7)));
                var ordinary = await store.StartAsync(false, id);
                Check(ordinary.Sections.Single().Names.Length == 8, "Disabled aggregation combined records");
                await store.SetSettingsAsync(state.Settings with { AggregateDonations = true }, id);
                var journal = Path.Combine(directory, "sessions", id + ".jsonl");
                var originalBytes = File.ReadAllBytes(journal);
                var run = await store.StartAsync(false, id);
                Check(run.Sections.Single().Names.SequenceEqual(new[] { "MiXeD — 6,60 USD", "Next — 4,00 USD", "mixed — 5,00 EUR", "Ёж — 30,00 RUB", "MiXeD" }),
                    "Case variants across sources/platforms must sum by currency and retain the first chronological spelling");
                Check(File.ReadAllBytes(journal).SequenceEqual(originalBytes) && (await store.StateAsync()).EventCount == 8,
                    "Aggregation rewrote source records or event counts");
                Check(ordinary.Sections.Single().Names.Length == 8, "Changing aggregation mutated the earlier snapshot");
                store.Record(new("oda", "other", "donation", "mIxEd", OccurredAtUtc: t.AddSeconds(8), Amount: .40m, Currency: "USD"));
                Check(run.Sections.Single().Names[0] == "MiXeD — 6,60 USD", "New mixed-case donation mutated frozen totals");
                Check((await store.StartAsync(false, id)).Sections.Single().Names[0] == "MiXeD — 7,00 USD", "Next snapshot missed a case-variant donation");
            }
            await using (var restored = new CreditStore(directory))
            {
                var state = await restored.StateAsync();
                Check(state.EventCount == 9 && (await restored.StartAsync(false, state.SessionId)).Sections.Single().Names[0] == "MiXeD — 7,00 USD",
                    "Restart lost totals or first spelling");
                await restored.SetSettingsAsync(state.Settings with { AggregateDonations = false }, state.SessionId);
                var names = (await restored.StartAsync(false, state.SessionId)).Sections.Single().Names;
                Check(names.Length == 9 && names[0] == "MiXeD — 1,10 USD" && names[2] == "mixed — 2,20 USD" && names[^1] == "mIxEd — 0,40 USD",
                    "Disabling aggregation did not restore each original spelling/amount");
            }
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    public static async Task CurrencyBoundary()
    {
        var directory = Temp();
        await using (var store = new CreditStore(directory))
        {
            var state = await store.StateAsync();
            await store.SetSettingsAsync(state.Settings with { AggregateDonations = true }, state.SessionId);
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var package = Path.Combine(Directory.GetCurrentDirectory(), "src", "RutonyCredits");
        using (var runtime = RuntimeHost.Start(package, directory, port))
        {
            runtime.RecordDonation("oda", "One", 1m, " usd ");
            runtime.RecordDonation("da", "One", 2m, "USD");
            runtime.RecordDonation("oda", "One", 3m, "$");
            runtime.RecordDonation("oda", "One", 4m, "EUR");
            using var http = new HttpClient();
            var state = await http.GetFromJsonAsync<CreditState>(new Uri(new Uri(runtime.ControlUrl), "/api/state"), Wire.Json);
            Check(state?.EventCount == 4, "Currency normalization dropped an event");
        }
        await using var restored = new CreditStore(directory);
        var id = (await restored.StateAsync()).SessionId;
        var names = (await restored.StartAsync(false, id)).Sections.Single().Names;
        Check(names.SequenceEqual(new[] { "One — 3,00 USD", "One", "One — 4,00 EUR" }),
            "USD/usd must combine; ambiguous symbols must retain a name-only event without guessing currency");
    }
}