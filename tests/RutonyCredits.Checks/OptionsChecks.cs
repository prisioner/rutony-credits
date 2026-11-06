using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using RutonyCredits;

internal static class OptionsChecks
{
    private static string DirectoryName() => Path.Combine(Path.GetTempPath(), "credits-options-" + Guid.NewGuid().ToString("N"));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static CreditSettings Settings(CreditSettings current, bool loop, bool donations)
    {
        var node = JsonSerializer.SerializeToNode(current, Wire.Json)!;
        node["loop"] = loop;
        node["enabledSections"] = JsonSerializer.SerializeToNode(new Dictionary<string,bool> {
            ["vk-raids"]=false, ["twitch-raids"]=false, ["donations"]=donations, ["followers"]=false, ["subscribers"]=false, ["moderators"]=false
        });
        return node.Deserialize<CreditSettings>(Wire.Json)!;
    }
    public static async Task SectionsAndLoop()
    {
        var directory = DirectoryName();
        try
        {
            await using var store = new CreditStore(directory);
            var id = (await store.StateAsync()).SessionId;
            store.Record(new("rutony","twitch","raid","Raid"));
            store.Record(new("rutony","vkvideolive","raid","VK Raid"));
            store.Record(new("rutony","other","donation","Donor"));
            store.Record(new("rutony","twitch","follower","Follower"));
            store.Record(new("rutony","twitch","subscriber","Subscriber"));
            await store.SetSettingsAsync(Settings((await store.StateAsync()).Settings, true, true), id);
            var run = await store.StartAsync(false,id);
            Check(run.Sections.Length == 1 && run.Sections[0].Key == "donations", "Disabled sections must not appear");
            Check(!await store.CompleteAsync(run.RunId,run.CompletionToken), "A looping run must not complete at the end of one pass");
            await store.SetSettingsAsync(Settings((await store.StateAsync()).Settings,false,false),id);
            var frozen = JsonSerializer.SerializeToNode(run.Settings,Wire.Json)!;
            Check(frozen["loop"]!.GetValue<bool>() && frozen["enabledSections"]!["donations"]!.GetValue<bool>(), "Running snapshot must retain loop and section mask");
            Check((await store.StateAsync()).EventCount == 5, "Visibility must not discard collected events");
            var empty = await store.StartAsync(true,id);
            Check(empty.Sections.Length == 0 && (await store.StateAsync()).Playback == "finished", "All disabled sections must finish even in preview");
            await store.StopAsync(id,empty.RunId);
            await store.SetSettingsAsync(Settings((await store.StateAsync()).Settings,true,true),id);
            await store.DisposeAsync();
            await using var restored = new CreditStore(directory);
            var saved = JsonSerializer.SerializeToNode((await restored.StateAsync()).Settings,Wire.Json)!;
            Check(saved["loop"]!.GetValue<bool>() && saved["enabledSections"]!["donations"]!.GetValue<bool>()
                && !saved["enabledSections"]!["followers"]!.GetValue<bool>(), "Loop and section choices must persist after restart");
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
    public static async Task DonationAmountsAndLegacy()
    {
        var directory = DirectoryName();
        try
        {
            await using(var original = new CreditStore(directory))
            { original.Record(new("rutony","other","donation","Legacy")); }
            var journal = Directory.GetFiles(Path.Combine(directory,"sessions"),"*.jsonl").Single();
            var legacy = JsonNode.Parse(File.ReadAllText(journal))!.AsObject();
            legacy.Remove("amount"); legacy.Remove("currency");
            File.WriteAllText(journal,legacy.ToJsonString(Wire.Json)+"\n");
            var oldSettings=JsonSerializer.SerializeToNode(new CreditSettings(),Wire.Json)!.AsObject();
            oldSettings.Remove("loop"); oldSettings.Remove("enabledSections");
            File.WriteAllText(Path.Combine(directory,"settings.json"),oldSettings.ToJsonString(Wire.Json));
            await using(var store = new CreditStore(directory))
            {
                var state = await store.StateAsync();
                Check(state.Error == null, "Old journal/settings must still load");
                foreach(var json in new[]{
                    """{"source":"rutony","platform":"other","kind":"donation","displayName":"Orbit","amount":12.34,"currency":"EUR"}""",
                    """{"source":"rutony","platform":"other","kind":"donation","displayName":"Orbit","amount":75,"currency":"RUB"}"""
                }) Check(store.Record(JsonSerializer.Deserialize<EventInput>(json,Wire.Json)!),"Valid monetary event rejected");
                var run = await store.StartAsync(false,state.SessionId);
                var names=run.Sections.Single(s=>s.Key=="donations").Names;
                Check(names.SequenceEqual(new[]{"Legacy","Orbit — 12,34 EUR","Orbit — 75,00 RUB"}),"Each donor entry must preserve its own amount/currency and chronology");
                var config=JsonSerializer.SerializeToNode(state.Settings,Wire.Json)!;
                Check(!config["loop"]!.GetValue<bool>() && config["enabledSections"]!.AsObject().All(p=>p.Value!.GetValue<bool>()),"Legacy defaults changed");
            }
            await using(var reopened = new CreditStore(directory))
            {
                var run=await reopened.StartAsync(false,(await reopened.StateAsync()).SessionId);
                Check(run.Sections[0].Names.Last()=="Orbit — 75,00 RUB","Monetary fields must survive restart");
            }
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
    public static async Task SystemFonts()
    {
        var directory=DirectoryName();
        try
        {
            await using var store=new CreditStore(directory);
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            await using var server=await CreditsServer.StartAsync(store,Path.Combine(Directory.GetCurrentDirectory(),"src","RutonyCredits","web"),port);
            using var http=new HttpClient();
            using var response=await http.GetAsync(server.BaseUrl+"/api/fonts");
            Check(response.IsSuccessStatusCode,"System fonts endpoint is unavailable");
            var names=JsonSerializer.Deserialize<string[]>(await response.Content.ReadAsStringAsync(),Wire.Json)!;
            if(OperatingSystem.IsWindows()) Check(names.Length>0,"Actual Windows catalog is empty");
            Check(names.Length==names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),"Font families must not repeat by style");
            Check(names.All(n=>n.Length is >0 and <=100 && !n.StartsWith('@') && !n.Any(char.IsControl)),"Invalid font family name");
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
}
