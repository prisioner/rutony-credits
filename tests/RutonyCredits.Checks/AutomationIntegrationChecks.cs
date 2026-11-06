using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using RutonyCredits;

internal static class AutomationIntegrationChecks
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task Until(Func<Task<bool>> test)
    {
        var timer=Stopwatch.StartNew();
        while(!await test()){if(timer.Elapsed>TimeSpan.FromSeconds(10))throw new Exception("Runtime integration timed out");await Task.Delay(15);}
    }
    public static async Task Run()
    {
        await using var fake=await ObsChecks.FakeObs.Start();
        var directory=Path.Combine(Path.GetTempPath(),"credits-runtime-obs-"+Guid.NewGuid().ToString("N"));
        var package=Path.Combine(Directory.GetCurrentDirectory(),"src","RutonyCredits");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var time = new MonoTimeProvider();
        RuntimeHost? runtime=null;
        var messages=new ConcurrentQueue<string>();
        using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+port)};
        async Task<CreditState> State()=>await http.GetFromJsonAsync<CreditState>("/api/state",Wire.Json)??throw new Exception("Missing state");
        try
        {
            runtime=RuntimeHost.StartWithTimeProvider(package,directory,port,time);
            runtime.SetNotificationHandler(messages.Enqueue);
            runtime.RecordDonation("other","Donor",42,"RUB");runtime.RecordDonation("other","Donor",8,"RUB");runtime.RecordModerator("twitch","Mod","user-id");
            var initial=await State();Check(initial.EventCount==3,"Initial data missing");
            Check(messages.IsEmpty,"Startup warning used events arriving after startup");
            var options=new ObsOptions{Enabled=true,Port=fake.Port,SceneName="Credits"};
            using(var denied=await http.PostAsJsonAsync("/api/obs",new{options}))Check(denied.StatusCode==HttpStatusCode.Forbidden,"OBS route lacks management-token guard");
            var html=await http.GetStringAsync("/control");var token=Regex.Match(html,"name=\"credits-token\" content=\"([A-F0-9]+)\"").Groups[1].Value;
            http.DefaultRequestHeaders.Add("X-Credits-Token",token);
            foreach (int delay in new[] {0, 121})
                using (var badDelay = await http.PostAsJsonAsync("/api/obs", new { options = options with { ResetDelayMinutes = delay } }))
                    Check(badDelay.StatusCode == HttpStatusCode.BadRequest, "HTTP accepted an invalid OBS reset delay");
            using(var bad=await http.PostAsJsonAsync("/api/obs",new{options,password=true}))Check(bad.StatusCode==HttpStatusCode.BadRequest,"Invalid password type accepted");
            using(var response=await http.PostAsJsonAsync("/api/settings",new{sessionId=initial.SessionId,settings=initial.Settings with{AggregateDonations=true}}))response.EnsureSuccessStatusCode();
            using(var response=await http.PostAsJsonAsync("/api/obs",new{options,password=OperatingSystem.IsWindows()?ObsChecks.FakeObs.Password:null,clearPassword=false}))response.EnsureSuccessStatusCode();
            var peer=await fake.NextPeer();
            await Until(async()=> (await http.GetFromJsonAsync<ObsStatus>("/api/obs",Wire.Json))!.Connected);
            Check((await State()).RunId==null,"Baseline started credits");
            Check(!(await http.GetStringAsync("/api/obs")).Contains(ObsChecks.FakeObs.Password),"HTTP state leaked password");
            await peer.Event("CurrentProgramSceneChanged",new{sceneName="Live"});
            await peer.Event("CurrentProgramSceneChanged",new{sceneName="Credits"});
            await Until(async()=>(await State()).Playback=="running");
            var playing=await State();var run=await http.GetFromJsonAsync<CreditRun>("/api/runs/"+playing.RunId,Wire.Json);
            Check(run!.Sections.Single(x=>x.Key=="donations").Names.Single()=="Donor — 50,00 RUB","OBS run did not use saved aggregation");
            Check(run.Sections.Single(x=>x.Key=="moderators").Names.Single()=="Mod","OBS run lost moderator section");
            await peer.Event("CurrentProgramSceneChanged",new{sceneName="Live"});await Until(async()=>(await State()).Playback=="idle");
            await peer.Stream("STARTING");await peer.Stream("STARTED");
            await Until(()=>Task.FromResult(messages.Any(x=>x.Contains("При старте трансляции OBS"))));
            Check((await State()).SessionId==initial.SessionId,"Stream start reset existing counters");
            await peer.Stream("RECONNECTING");await peer.Stream("RECONNECTED");await peer.Stream("STOPPED");
            await Until(()=>Task.FromResult(messages.Any(x=>x.Contains("Статистика сохранена",StringComparison.OrdinalIgnoreCase))));
            Check((await State()).EventCount==3,"Unexpected stream stop discarded counters");
            await peer.Stream("STARTED");await peer.Stream("STOPPING");await peer.Stream("STOPPED");
            await Until(async()=> (await http.GetFromJsonAsync<ObsStatus>("/api/obs",Wire.Json))!.PendingResetUntilUtc != null);
            Check((await State()).EventCount==3,"Confirmed stop reset counters before the delay");
            time.Advance(TimeSpan.FromMinutes(5));
            await Until(async()=>(await State()).SessionId!=initial.SessionId);
            Check((await State()).EventCount==0,"Confirmed stop failed to clear period");
            Check(File.ReadLines(Path.Combine(directory,"sessions",initial.SessionId+".jsonl")).Count()==3,"Stop destroyed old journal");
            runtime.Record("twitch","follower","After stop");
            await peer.Stream("STARTED"); await peer.Stream("STOPPING"); await peer.Stream("STOPPED");
            await Until(async()=> (await http.GetFromJsonAsync<ObsStatus>("/api/obs",Wire.Json))!.PendingResetUntilUtc != null);
            var beforeManual = await State();
            using (var response = await http.PostAsJsonAsync("/api/reset", new { sessionId = beforeManual.SessionId })) response.EnsureSuccessStatusCode();
            Check((await http.GetFromJsonAsync<ObsStatus>("/api/obs",Wire.Json))!.PendingResetUntilUtc == null, "Manual HTTP reset left the pending indicator active");
            runtime.Record("twitch", "follower", "After manual reset"); var retained = await State();
            time.Advance(TimeSpan.FromMinutes(10));
            Check((await State()).SessionId == retained.SessionId && (await State()).EventCount == 1, "Old OBS timer cleared new HTTP period");
            fake.InitialStreaming=false;peer.Abort();peer=await fake.NextPeer();
            await Until(async()=> (await http.GetFromJsonAsync<ObsStatus>("/api/obs",Wire.Json))!.Connected);
            Check((await State()).SessionId==retained.SessionId && (await State()).EventCount==1,"OBS restart cleared period");
            runtime.Dispose();runtime=null;
            runtime=RuntimeHost.StartWithTimeProvider(package,directory,port,time);runtime.SetNotificationHandler(messages.Enqueue);
            await Until(()=>Task.FromResult(messages.Any(x=>x.Contains("При включении Rutony Credits")&&x.Contains("/control"))));
            Check((await State()).SessionId==retained.SessionId && (await State()).EventCount==1,"Rutony restart cleared missed-stop statistics");
        }
        finally{runtime?.Dispose();}
    }
}
