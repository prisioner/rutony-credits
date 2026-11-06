using System.Text.Json;
using System.Text.Json.Nodes;
using RutonyCredits;

internal static class AutomationChecks
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "credits-automation-" + Guid.NewGuid().ToString("N"));
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static CreditSettings Aggregate(CreditSettings settings, bool enabled)
    {
        var node=JsonSerializer.SerializeToNode(settings,Wire.Json)!;
        node["aggregateDonations"]=enabled;
        return node.Deserialize<CreditSettings>(Wire.Json)!;
    }
    public static async Task Donations()
    {
        var dir=Temp();
        await using(var s=new CreditStore(dir))
        {
            var id=(await s.StateAsync()).SessionId;
            var t=DateTimeOffset.Parse("2026-01-01T12:00:00Z");
            s.Record(new("oda","other","donation","Orbit",OccurredAtUtc:t,Amount:10.25m,Currency:"RUB"));
            s.Record(new("da","other","donation","Next",OccurredAtUtc:t.AddSeconds(1),Amount:5m,Currency:"RUB"));
            s.Record(new("da","other","donation","Orbit",OccurredAtUtc:t.AddSeconds(2),Amount:20m,Currency:"rub"));
            s.Record(new("oda","other","donation","Orbit",OccurredAtUtc:t.AddSeconds(3),Amount:3m,Currency:"USD"));
            s.Record(new("oda","other","donation","orbit",OccurredAtUtc:t.AddSeconds(4),Amount:4m,Currency:"RUB"));
            s.Record(new("oda","other","donation","Orbit"));
            var before=await s.StartAsync(false,id);
            Check(before.Sections.Single().Names.Length==6,"Default must retain separate donations");
            await s.SetSettingsAsync(Aggregate((await s.StateAsync()).Settings,true),id);
            var run=await s.StartAsync(false,id);
            var names=run.Sections.Single().Names;
            Check(names.SequenceEqual(new[]{"Orbit — 34,25 RUB","Next — 5,00 RUB","Orbit — 3,00 USD","Orbit"}),"Group by case-insensitive nickname+currency, keep first spelling, chronology and unknown rows");
            Check((await s.StateAsync()).EventCount==6,"Aggregation must not rewrite event counts");
            Check(before.Sections.Single().Names.Length==6,"Existing snapshot changed");
            s.Record(new("oda","other","donation","Orbit",Amount:1m,Currency:"RUB"));
            Check(run.Sections.Single().Names[0]=="Orbit — 34,25 RUB","Live donation mutated frozen snapshot");
            Check((await s.StartAsync(false,id)).Sections.Single().Names[0]=="Orbit — 35,25 RUB","Next snapshot missed extra amount");
        }
        await using(var s=new CreditStore(dir))
        {
            var state=await s.StateAsync();
            Check(JsonSerializer.SerializeToNode(state.Settings,Wire.Json)!["aggregateDonations"]!.GetValue<bool>(),"Aggregate flag did not persist");
            Check((await s.StartAsync(false,state.SessionId)).Sections.Single().Names[0]=="Orbit — 35,25 RUB","Persisted donations changed");
        }
    }
    public static async Task OverflowAndLegacy()
    {
        await using var s=new CreditStore(Temp());var id=(await s.StateAsync()).SessionId;
        s.Record(new("oda","other","donation","Large",Amount:decimal.MaxValue,Currency:"USD"));
        s.Record(new("da","other","donation","large",Amount:1,Currency:"USD"));
        s.Record(new("oda","other","donation","large",Amount:2,Currency:"usd"));
        await s.SetSettingsAsync(Aggregate((await s.StateAsync()).Settings,true),id);
        Check((await s.StartAsync(false,id)).Sections.Single().Names.Length==3,"Mixed-case overflow must retain all original rows, not lose money");
        var node=JsonSerializer.SerializeToNode((await s.StateAsync()).Settings,Wire.Json)!;
        node["headings"]!.AsObject().Remove("moderators");node["enabledSections"]!.AsObject().Remove("moderators");node.AsObject().Remove("aggregateDonations");
        await s.SetSettingsAsync(node.Deserialize<CreditSettings>(Wire.Json)!,id);
        var settings=(await s.StateAsync()).Settings;
        Check(settings.Headings.ContainsKey("moderators") && settings.EnabledSections["moderators"],"Legacy five sections were not upgraded");
        Check(!JsonSerializer.SerializeToNode(settings,Wire.Json)!["aggregateDonations"]!.GetValue<bool>(),"Legacy aggregation default changed");
    }
    public static async Task Moderators()
    {
        var dir=Temp();string id;
        await using(var s=new CreditStore(dir))
        {
            id=(await s.StateAsync()).SessionId;
            Check(s.Record(new("rutony","twitch","moderator","Mod One","mod-1")),"Moderator input rejected");
            s.Record(new("rutony","twitch","moderator","Mod One","mod-1"));
            s.Record(new("rutony","vkvideolive","moderator","mod one","mod-vk-1"));
            s.Record(new("rutony","twitch","moderator","Mod Two","mod-2"));
            var state=await s.StateAsync();
            Check(state.Counts["moderators"]==2,"Moderator count must match unique displayed nicknames");
            var section=(await s.StartAsync(false,id)).Sections.Single();
            Check(section.Key=="moderators" && section.Names.SequenceEqual(new[]{"Mod One","Mod Two"}),"Moderator section duplicates or order changed");
            await s.SetSettingsAsync(state.Settings with{EnabledSections=new(state.Settings.EnabledSections){["moderators"]=false}},id);
            Check((await s.StartAsync(false,id)).Sections.Length==0,"Moderator section cannot be hidden");
        }
        await using(var s=new CreditStore(dir))
        {
            var state=await s.StateAsync();Check(state.Counts["moderators"]==2,"Moderator restore failed");
            await s.ResetAsync(id);Check((await s.StateAsync()).EventCount==0,"Moderator did not reset with session");
            Check(File.ReadLines(Path.Combine(dir,"sessions",id+".jsonl")).Count()==3,"Previous moderator journal was lost");
        }
    }
}
