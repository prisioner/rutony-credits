using RutonyCredits;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

internal static class FontFaceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run()
    {
        var selected = SystemFonts.SelectFaces([
            new("Regular localized", 400, false), new("Regular localized", 400, false),
            new("Полужирный localized", 700, false), new("Italic", 400, true)]);
        Check(selected.Length == 2 && selected[0] == new SystemFonts.LocalFace("Regular localized",400)
            && selected[1] == new SystemFonts.LocalFace("Полужирный localized",700), "Select ordinary and localized bold without charset duplicates/italic");
        foreach (var weight in new[] {250,600,900})
        {
            selected = SystemFonts.SelectFaces([new("Single",weight,false),new("Single Italic",weight,true)]);
            Check(selected.Length == 1 && selected[0].Weight == 400, "Single face must retain browser bold synthesis regardless of native weight");
        }
        selected = SystemFonts.SelectFaces([new("Light",300,false),new("Medium",500,false),new("Heavy",800,false)]);
        Check(selected[0].LocalName == "Light" && selected[1].LocalName == "Heavy", "Closest native weight selection is deterministic");
        Check(SystemFonts.SelectFaces([new("bad\nname",400,false),new("Italic",400,true)]).Length == 0, "Unsafe/italic metadata rejected");
        var root = Path.Combine(Path.GetTempPath(),"rutony-font-checks",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new CreditStore(root);
            var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            await using var server = await CreditsServer.StartAsync(store,root,port);
            using var http = new HttpClient {BaseAddress = new Uri(server.BaseUrl)};
            Check((await http.GetFromJsonAsync<SystemFonts.LocalFace[]>("/api/font-faces?family=__rutony_missing_7cde"))!.Length == 0,"Unknown family must not receive GDI fallback");
            foreach (var query in new[] {"", "?family=", "?family="+new string('x',101),"?family=Bad%0AName","?family=Arial&family=Tahoma"})
                Check((await http.GetAsync("/api/font-faces"+query)).StatusCode == HttpStatusCode.BadRequest,"Invalid font query must be bounded: "+query);
            if (OperatingSystem.IsWindows())
            {
                var names = await http.GetFromJsonAsync<string[]>("/api/fonts");
                Check(names!.Contains("Arial",StringComparer.OrdinalIgnoreCase),"Windows generic Arial prerequisite missing");
                var faces = await http.GetFromJsonAsync<SystemFonts.LocalFace[]>("/api/font-faces?family=Arial");
                Check(faces!.Length == 2 && faces[0].Weight == 400 && faces[1].Weight == 700
                    && !StringComparer.OrdinalIgnoreCase.Equals(faces[0].LocalName,faces[1].LocalName),"Actual GDI Arial ordinary/bold metadata");
            }
        }
        finally { Directory.Delete(root,true); }
    }
}
