using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using RutonyCredits;

internal static class ObsRecoveryChecks
{
    public static async Task Run()
    {
        foreach(var malformed in new[]{"null",new string('x',65537)})
        {
            var directory=Path.Combine(Path.GetTempPath(),"credits-obs-recovery-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"obs.json"),malformed);
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            using var runtime=RuntimeHost.Start(Path.Combine(Directory.GetCurrentDirectory(),"src","RutonyCredits"),directory,port);
            runtime.Record("twitch","follower","Recovery");
            using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+port)};
            var state=await http.GetFromJsonAsync<CreditState>("/api/state",Wire.Json);
            if(state?.EventCount!=1)throw new Exception("OBS settings failure disabled event recording");
            var obs=await http.GetFromJsonAsync<ObsStatus>("/api/obs",Wire.Json);
            if(obs?.Error==null || obs.Connected || obs.Options.Enabled)throw new Exception("OBS settings failure was not isolated");
        }
        var recoverDirectory=Path.Combine(Path.GetTempPath(),"credits-obs-recovery-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recoverDirectory);File.WriteAllText(Path.Combine(recoverDirectory,"obs.json"),"null");
        await using var controller=new ObsController(recoverDirectory,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask,_=>{});
        await controller.ConfigureAsync(new ObsOptions());
        if(controller.Status.Error!=null || controller.Status.Options.Enabled)throw new Exception("OBS settings did not recover after save");
    }
}
