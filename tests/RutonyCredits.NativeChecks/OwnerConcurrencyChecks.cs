using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using RutonyChat;
using RutonyChat.ChatServices;
using RutonyCredits;

internal static class OwnerConcurrencyChecks
{
    private static async Task Until(Func<Task<bool>> condition)
    {for(int i=0;i<200;i++){if(await condition())return;await Task.Delay(10);}throw new Exception("Owner concurrency check timed out");}
    public static async Task Run(string package)
    {
        foreach(bool sameApi in new[]{false,true})
        {
            var directory=Path.Combine(Path.GetTempPath(),"credits-owner-race-"+Guid.NewGuid().ToString("N"));
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            var script=new Script();
            var gate=new TaskCompletionSource<global::OAuth.VKPlayAPI.ChannelInfoItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            var apiA=new global::OAuth.VKPlayAPI{Gate=gate};
            var service=new FakeVkService{ServiceAPI=apiA,PathValue="https://live.vkvideo.ru/channel-a"};
            ChatServer.Services[ProgramProps.SiteEnum.vkplay]=service;
            using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+port)};
            try
            {
                script.InitParams(new(){["PackageDirectory"]=package,["DataDirectory"]=directory,["Port"]=port.ToString()});
                await Until(()=>Task.FromResult(apiA.Requests==1));
                var apiB=sameApi ? apiA : new global::OAuth.VKPlayAPI();
                apiB.Gate=null;apiB.Result=new(){data=new(){owner=new(){id=200,nick="Current owner B"}}};
                if(sameApi) service.PathValue="https://live.vkvideo.ru/channel-b";
                else ChatServer.Services[ProgramProps.SiteEnum.vkplay]=new FakeVkService{ServiceAPI=apiB,PathValue="https://live.vkvideo.ru/channel-b"};
                ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Current owner B",true,"200");
                ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Old owner A",true,"100");
                await Until(async()=>apiB.Requests==(sameApi?2:1)&&(await http.GetFromJsonAsync<CreditState>("/api/state",Wire.Json))!.Counts.GetValueOrDefault("moderators")==1);
                gate.SetResult(new(){data=new(){owner=new(){id=100,nick="Old owner A"}}});
                await Task.Delay(150);
                ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Current owner B",true,"200");
                var state=(await http.GetFromJsonAsync<CreditState>("/api/state",Wire.Json))!;
                var html=await http.GetStringAsync("/control");var token=Regex.Match(html,"name=\"credits-token\" content=\"([A-F0-9]+)\"").Groups[1].Value;
                http.DefaultRequestHeaders.Add("X-Credits-Token",token);
                using var response=await http.PostAsJsonAsync("/api/run",new{sessionId=state.SessionId});response.EnsureSuccessStatusCode();
                var run=await response.Content.ReadFromJsonAsync<CreditRun>(Wire.Json);
                var names=run!.Sections.Single(x=>x.Key=="moderators").Names;
                if(!names.SequenceEqual(new[]{"Old owner A"}))throw new Exception("Late VK response replaced current owner after "+(sameApi?"same-API channel change":"API replacement"));
            }
            finally{script.Closing();ChatServer.Services.Clear();}
        }
        Console.WriteLine("PASS native VK current API/channel guards reject old lookup responses");
    }
}
