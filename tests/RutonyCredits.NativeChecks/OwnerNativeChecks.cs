using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using RutonyChat;
using RutonyChat.ChatServices;
using RutonyCredits;

internal static class OwnerNativeChecks
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static int Port(){var s=new TcpListener(IPAddress.Loopback,0);s.Start();int p=((IPEndPoint)s.LocalEndpoint).Port;s.Stop();return p;}
    private static async Task Until(Func<Task<bool>> test){for(int i=0;i<200;i++){if(await test())return;await Task.Delay(10);}throw new Exception("Native owner/link check timed out");}
    public static async Task Run(string package)
    {
        var directory=Path.Combine(Path.GetTempPath(),"credits-owner-native-"+Guid.NewGuid().ToString("N"));
        var port=Port();var script=new Script();
        var parameters=new Dictionary<string,string>{["PackageDirectory"]=package,["DataDirectory"]=directory,["Port"]=port.ToString()};
        var api=new global::OAuth.VKPlayAPI{Result=new(){data=new(){owner=new(){id=77,nick="Presenter VK"}}}};
        ChatServer.Services[ProgramProps.SiteEnum.twitch]=new ChatService{PathValue="https://twitch.tv/StreamOwner"};
        ChatServer.Services[ProgramProps.SiteEnum.vkplay]=new FakeVkService{ServiceAPI=api,PathValue="https://live.vkvideo.ru/channel-slug"};
        using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+port)};
        try
        {
            script.InitParams(parameters);
            Check(RutonyBot.Messages.Any(m=>m.Contains("<a href=\"http://127.0.0.1:"+port+"/control\"")&&m.Contains("target=\"_blank\"")&&m.Contains("noopener noreferrer")),"Credits initialization lacks explicit clickable local link");
            var runtime=(RuntimeHost)typeof(Script).GetField("runtime",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(script)!;
            var notify=(Action<string>)typeof(RuntimeHost).GetField("notification",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime)!;
            notify("<b>unsafe</b> http://127.0.0.1:"+port+"/control");
            await Until(()=>Task.FromResult(RutonyBot.Messages.Any(m=>m.Contains("&lt;b&gt;unsafe&lt;/b&gt;")&&m.Contains("<a href="))));
            ChatEventServer.Message(ProgramProps.SiteEnum.twitch,"STREAMOWNER",true,"twitch-owner");
            ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Changed owner display",true,"77");
            for(int i=0;i<10;i++)ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Actual Moderator",true,"78");
            await Until(async()=>api.Requests==1&&(await http.GetFromJsonAsync<CreditState>("/api/state",Wire.Json))!.Counts.GetValueOrDefault("moderators")==1);
            Check(api.Requests==1,"Repeated chat messages triggered repeated VK owner GETs");
            script.Closing();
            api=new global::OAuth.VKPlayAPI{State=global::OAuth.OAuthBase.StatesAuth.idle};
            ChatServer.Services[ProgramProps.SiteEnum.vkplay]=new FakeVkService{ServiceAPI=api,PathValue="https://live.vkvideo.ru/channel-slug"};
            script.InitParams(parameters);ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Unauthenticated observer",true,"79");
            await Task.Delay(30);Check(api.Requests==0,"Owner lookup ran without existing ready OAuth");
            script.Closing();
            var gate=new TaskCompletionSource<global::OAuth.VKPlayAPI.ChannelInfoItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            api=new global::OAuth.VKPlayAPI{Gate=gate};
            ChatServer.Services[ProgramProps.SiteEnum.vkplay]=new FakeVkService{ServiceAPI=api,PathValue="https://live.vkvideo.ru/channel-slug"};
            script.InitParams(parameters);await Until(()=>Task.FromResult(api.Requests==1));
            var closing=Task.Run(()=>script.Closing());await closing.WaitAsync(TimeSpan.FromSeconds(5));
            ChatServer.Services.Remove(ProgramProps.SiteEnum.vkplay);
            script.InitParams(parameters);
            var beforeLate=(await http.GetFromJsonAsync<CreditState>("/api/state",Wire.Json))!.Counts.GetValueOrDefault("moderators");
            ChatEventServer.Message(ProgramProps.SiteEnum.vkplay,"Late owner",true,"999");
            gate.SetResult(new(){data=new(){owner=new(){id=999,nick="Late owner"}}});
            await Task.Delay(60);
            var state=await http.GetFromJsonAsync<CreditState>("/api/state",Wire.Json);
            Check(state!.Counts.GetValueOrDefault("moderators")==beforeLate+1,"Late result from closed owner lookup changed a new runtime");
            Console.WriteLine("PASS native explicit links/escaping, automatic Twitch/VK owners, ready-auth guard, once-only lookup and stale-result shutdown");
        }
        finally{script.Closing();ChatServer.Services.Clear();}
    }
}
