using System.Text.Json;
using System.Text.Json.Nodes;
using RutonyCredits;

internal static class ModeratorExclusionChecks
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"credits-exclusions-"+Guid.NewGuid().ToString("N"));
    private static CreditSettings WithList(CreditSettings settings,params string[] names)
    {
        var node=JsonSerializer.SerializeToNode(settings,Wire.Json)!;node["moderatorExclusions"]=JsonSerializer.SerializeToNode(names,Wire.Json);
        return node.Deserialize<CreditSettings>(Wire.Json)!;
    }
    public static async Task Exclusions()
    {
        var directory=Temp();string id;
        await using(var s=new CreditStore(directory))
        {
            id=(await s.StateAsync()).SessionId;
            s.Record(new("rutony","twitch","moderator","Build Bot","bot"));
            s.Record(new("rutony","vkvideolive","moderator","Модератор Один","mod"));
            s.Record(new("rutony","twitch","donation","Build Bot",Amount:2,Currency:"RUB"));
            var original=await s.StartAsync(false,id);
            await s.SetSettingsAsync(WithList((await s.StateAsync()).Settings," @build bot "),id);
            var filtered=await s.StartAsync(false,id);
            Check(filtered.Sections.Single(x=>x.Key=="moderators").Names.SequenceEqual(new[]{"Модератор Один"}),"Exclusions did not match nickname/case/@");
            Check((await s.StateAsync()).Counts["moderators"]==1,"Counter includes hidden moderator");
            Check(filtered.Sections.Single(x=>x.Key=="donations").Names.Single()=="Build Bot — 2,00 RUB","Exclusions changed a different section");
            Check(original.Sections.Single(x=>x.Key=="moderators").Names.Length==2,"Old frozen run was mutated");
            Check((await s.StateAsync()).EventCount==2,"Total includes excluded historical moderator");
            Check(File.ReadAllLines(Path.Combine(directory,"sessions",id+".jsonl")).Length==3,"Exclusions deleted journal entries");
            var returned=JsonSerializer.SerializeToNode((await s.StateAsync()).Settings,Wire.Json)!;
            Check(returned["moderatorExclusions"]!.AsArray().Count==1,"List not returned");
        }
        await using(var s=new CreditStore(directory))
        {
            Check((await s.StateAsync()).Counts["moderators"]==1,"Exclusions not restored");
            await s.SetSettingsAsync(WithList((await s.StateAsync()).Settings),id);
            Check((await s.StartAsync(false,id)).Sections.Single(x=>x.Key=="moderators").Names.Length==2,"Removing exclusion failed to restore collected names");
            var valid=CreditStore.Validate(WithList((await s.StateAsync()).Settings," @Build Bot ","build bot","Тестовый бот"));
            var json=JsonSerializer.SerializeToNode(valid,Wire.Json)!;
            Check(json["moderatorExclusions"]!.AsArray().Count==2,"List not canonicalized/deduplicated");
            var property=typeof(CreditSettings).GetProperty("ModeratorExclusions")!;
            await s.SetSettingsAsync(valid,id);
            ((string[])property.GetValue(valid)!)[0]="Changed input";
            var read=(await s.StateAsync()).Settings;((string[])property.GetValue(read)!)[0]="Changed state";
            Check((await s.StateAsync()).Counts["moderators"]==1,"List aliases mutable external array");
            foreach(var invalid in new[]{new string[51],new[]{new string('x',201)},new[]{"bad\nname"}})
            {
                try{CreditStore.Validate(WithList(valid,invalid));throw new Exception("Invalid list accepted");}catch(ArgumentException){}
            }
            var legacy=JsonSerializer.SerializeToNode(valid,Wire.Json)!;legacy.AsObject().Remove("moderatorExclusions");
            var loaded=legacy.Deserialize<CreditSettings>(Wire.Json)!;
            Check(((string[])property.GetValue(CreditStore.Validate(loaded))!).Length==0,"Legacy default is not empty");
        }
    }
    public static async Task Owners()
    {
        await using var s=new CreditStore(Temp());var id=(await s.StateAsync()).SessionId;
        s.Record(new("rutony","twitch","moderator","StreamOwner","owner-twitch"));
        s.Record(new("rutony","vkvideolive","moderator","Old Display Name","owner-vk-id"));
        s.Record(new("rutony","vkvideolive","moderator","Actual Moderator","mod-id"));
        var before=await s.StartAsync(false,id);
        var method=typeof(CreditStore).GetMethod("ObserveChannelOwner")??throw new Exception("Owner observation boundary missing");
        method.Invoke(s,new object?[]{"twitch","streamowner",null});
        method.Invoke(s,new object?[]{"vkvideolive","New Display Name","owner-vk-id"});
        Check((await s.StateAsync()).Counts["moderators"]==1,"Owner nickname or stable ID was not filtered");
        Check((await s.StartAsync(false,id)).Sections.Single().Names.Single()=="Actual Moderator","Owner remained in credits");
        Check(before.Sections.Single().Names.Length==3,"Owner discovery mutated frozen snapshot");
        Check((await s.StateAsync()).EventCount==3,"Owner discovery rewrote journal");
    }
    public static async Task OwnerCache()
    {
        var directory=Temp();string id;
        await using(var store=new CreditStore(directory))
        {
            id=(await store.StateAsync()).SessionId;
            store.Record(new("rutony","twitch","moderator","Owner","owner-id"));
            store.Record(new("rutony","twitch","moderator","Mod","mod-id"));
            store.ObserveChannelContext("twitch", "channel-a");
            store.ObserveChannelOwnerForContext("twitch", "channel-a", "Owner", "owner-id");
            Check((await store.StateAsync()).Counts["moderators"]==1,"Owner observation missing");
        }
        await using(var restored=new CreditStore(directory))
        {
            restored.ObserveChannelContext("twitch", "channel-a");
            Check((await restored.StateAsync()).Counts["moderators"]==1,"Known owner returned after current channel was confirmed");
        }
        File.WriteAllText(Path.Combine(directory,"sessions",id+".owners.json"),"null");
        await using var badCache=new CreditStore(directory);
        Check((await badCache.StateAsync()).Error==null && (await badCache.StateAsync()).EventCount==2,"Optional owner cache corrupted primary journal");
    }

}
