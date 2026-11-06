using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using RutonyCredits;
using RutonyChat;

await WorkshopChecks.Run();

var directory=Path.Combine(Path.GetTempPath(),"credits-native-"+Guid.NewGuid().ToString("N"));
var package=Path.Combine(Directory.GetCurrentDirectory(),"src","RutonyCredits","bin","Release","net9.0");
var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
var oldCulture=CultureInfo.CurrentCulture;CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");
var script=new Script();
var parameters=new Dictionary<string,string> {["PackageDirectory"]=package,["DataDirectory"]=directory,["Port"]=port.ToString(CultureInfo.InvariantCulture)};
try
{
    script.InitParams(parameters);
    ChatEventServer.Donate("Orbit",12.34f,"eur");
    ChatEventServer.Donate("Orbit",123456.78f,"RUB");
    ChatEventServer.Donate("Legacy",null,null);
    ChatEventServer.Message(ProgramProps.SiteEnum.twitch,"Known Mod",true,"mod-user-one");
    ChatEventServer.Message(ProgramProps.SiteEnum.twitch,"Known Mod",true,"mod-user-one");
    ChatEventServer.Message(ProgramProps.SiteEnum.twitch,"Regular",false,"regular");
    ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Missing role",null,"missing");
    ChatEventServer.Message(ProgramProps.SiteEnum.other,"Other platform",true,"other");
    using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+port)};
    using var state=JsonDocument.Parse(await http.GetStringAsync("/api/state"));
    if(state.RootElement.GetProperty("eventCount").GetInt32()!=4)throw new Exception("Native events lost or moderator role mapping missing");
    var html=await http.GetStringAsync("/control");
    var token=Regex.Match(html,"name=\"credits-token\" content=\"([A-F0-9]+)\"").Groups[1].Value;
    http.DefaultRequestHeaders.Add("X-Credits-Token",token);
    using(var response=await http.PostAsJsonAsync("/api/run",new{sessionId=state.RootElement.GetProperty("sessionId").GetString()}))
        response.EnsureSuccessStatusCode();
    using var current=JsonDocument.Parse(await http.GetStringAsync("/api/state"));
    var id=current.RootElement.GetProperty("runId").GetString();
    using var run=JsonDocument.Parse(await http.GetStringAsync("/api/runs/"+id));
    var names=run.RootElement.GetProperty("sections")[0].GetProperty("names").EnumerateArray().Select(n=>n.GetString()).ToArray();
    if(!names.SequenceEqual(new[]{"Orbit — 12,34 EUR","Orbit — 123456,78 RUB","Legacy"}))throw new Exception("Actual native script did not preserve donation amounts/currency in ru-RU");
    var moderators=run.RootElement.GetProperty("sections").EnumerateArray().Single(section=>section.GetProperty("key").GetString()=="moderators").GetProperty("names");
    if(moderators.GetArrayLength()!=1 || moderators[0].GetString()!="Known Mod")throw new Exception("Native moderator duplicates/role filtering failed");
    if(Directory.GetFiles(Path.Combine(directory,"sessions")).Any(file=>File.ReadAllText(file).Contains("PRIVATE_TEST_CHAT_TEXT_MUST_NOT_BE_STORED")))throw new Exception("Chat text persisted");
    script.Closing();
    ChatEventServer.Message(ProgramProps.SiteEnum.twitch,"After closing mod",true,"after-closing");
    ChatEventServer.Donate("After closing",5,"RUB");
    var uiContext=new PumpContext();
    SynchronizationContext.SetSynchronizationContext(uiContext);
    try { script.InitParams(parameters); } finally { SynchronizationContext.SetSynchronizationContext(null); }
    using var restored=JsonDocument.Parse(await http.GetStringAsync("/api/state"));
    if(restored.RootElement.GetProperty("eventCount").GetInt32()!=4)throw new Exception("Native events remained subscribed after Closing");
    bool HasStartupWarning()=>RutonyBot.Messages.Any(message=>message.Contains("При включении Rutony Credits")&&message.Contains("http://127.0.0.1:"+port+"/control"));
    for(int i=0;i<100 && !HasStartupWarning();i++)await Task.Delay(10);
    if(!HasStartupWarning())throw new Exception("Nonempty-period startup warning requires pumping the captured UI context");
    var runtime=(RuntimeHost)typeof(Script).GetField("runtime",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(script)!;
    var staleNotification=(Action<string>)typeof(RuntimeHost).GetField("notification",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime)!;
    var closing=Task.Run(()=>script.Closing());
    await closing.WaitAsync(TimeSpan.FromSeconds(5));
    var sent=RutonyBot.Messages.Count;
    staleNotification("STALE_NATIVE_WARNING_AFTER_CLOSE");
    if(RutonyBot.Messages.Count!=sent)throw new Exception("Closed native instance emitted a stale warning");
    script.InitParams(parameters);
    sent=RutonyBot.Messages.Count;
    staleNotification("STALE_NATIVE_WARNING_AFTER_REINIT");
    if(RutonyBot.Messages.Count!=sent)throw new Exception("Replaced native instance emitted a stale warning");
    Console.WriteLine("PASS actual Credits script -> money and unique moderators without chat text; unsubscribe/reinit; warning without UI pump and stale-instance protection");
}
finally
{
    script.Closing();CultureInfo.CurrentCulture=oldCulture;
    if(Directory.Exists(directory))Directory.Delete(directory,true);
}

await OwnerNativeChecks.Run(package);
await OwnerConcurrencyChecks.Run(package);
await OwnerContextNativeChecks.Run(package);
await InvalidContextNativeChecks.Run(package);
await RaidNativeChecks.Run(package);

sealed class PumpContext:SynchronizationContext
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback,object State)> work=new();
    public int Pending=>work.Count;
    public override void Post(SendOrPostCallback callback,object state)=>work.Enqueue((callback,state));
    public override void Send(SendOrPostCallback callback,object state)=>throw new Exception("Synchronous UI dispatch is forbidden");
    public void Drain(){while(work.TryDequeue(out var item))item.Callback(item.State);}
}
