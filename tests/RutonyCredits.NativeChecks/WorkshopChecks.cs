using System.Globalization;
using System.Net;
using System.Net.Sockets;
using RutonyChat;

static class WorkshopChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "workshop-" + Guid.NewGuid().ToString("N"), "Пакеты [1]");
        var manual = Path.Combine(AppContext.BaseDirectory, "scripts", "RutonyCredits");
        if (Directory.Exists(manual)) throw new Exception("Test refuses to replace an existing manual fixture");
        var registered = Path.Combine(root, "Подписка [2]");
        var workshop = Path.Combine(registered, "RutonyCredits");
        var explicitPackage = Path.Combine(root, "Явный [3]");
        var data = Path.Combine(root, "isolated-data");
        void Package(string path, string marker)
        {
            Directory.CreateDirectory(Path.Combine(path, "web"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "RutonyCredits.dll"), Path.Combine(path, "RutonyCredits.dll"));
            File.WriteAllText(Path.Combine(path, "web", "control.html"), "<html>" + marker + "</html>");
        }
        async Task Start(string explicitPath, string marker)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var script = new Script();
            var options = new Dictionary<string,string> { ["DataDirectory"] = data, ["Port"] = port.ToString(CultureInfo.InvariantCulture) };
            if (explicitPath != null) options["PackageDirectory"] = explicitPath;
            try
            {
                script.InitParams(options);
                using var client = new HttpClient();
                var html = await client.GetStringAsync("http://127.0.0.1:" + port + "/control");
                if (!html.Contains(marker)) throw new Exception("Loader selected wrong package; expected " + marker);
                if (!Directory.Exists(data)) throw new Exception("Explicit synthetic data directory was ignored");
            }
            finally { script.Closing(); script.Closing(); }
            using var reuse = new TcpListener(IPAddress.Loopback, port); reuse.Start();
        }
        try
        {
            Package(manual, "MANUAL"); Package(workshop, "WORKSHOP"); Package(explicitPackage, "EXPLICIT");
            RutonyBotFunctions.RegisteredDirectory = registered;
            RutonyBotFunctions.Lookups.Clear();
            await Start(null, "WORKSHOP");
            if (!RutonyBotFunctions.Lookups.SequenceEqual(new[] { "rutony_credits.cs" })) throw new Exception("Wrong SDK script filename");
            RutonyBotFunctions.Lookups.Clear();
            RutonyBotFunctions.ThrowOnLookup = true;
            await Start(explicitPackage, "EXPLICIT");
            if (RutonyBotFunctions.Lookups.Count != 0) throw new Exception("Explicit package must bypass SDK lookup");
            RutonyBotFunctions.ThrowOnLookup = false;
            foreach (var empty in new string[] { null, "", "  " })
            {
                RutonyBotFunctions.RegisteredDirectory = empty;
                await Start(null, "MANUAL");
            }
            RutonyBotFunctions.ThrowOnLookup = true;
            await Start("  ", "MANUAL");
            RutonyBotFunctions.ThrowOnLookup = false;
            RutonyBotFunctions.RegisteredDirectory = registered;
            File.Delete(Path.Combine(workshop, "RutonyCredits.dll"));
            bool failed = false;
            try { await Start(null, "MUST_FAIL"); } catch (FileNotFoundException) { failed = true; }
            if (!failed) throw new Exception("Missing registered DLL silently loaded stale manual DLL");
            File.Delete(Path.Combine(explicitPackage, "RutonyCredits.dll"));
            failed = false;
            try { await Start(explicitPackage, "MUST_FAIL"); } catch (FileNotFoundException) { failed = true; }
            if (!failed) throw new Exception("Missing explicit DLL silently fell back");
            if (Directory.GetFiles(workshop, "*", SearchOption.AllDirectories).Length != 1)
                throw new Exception("Runtime data leaked into Workshop package");
            Console.WriteLine("PASS Workshop SDK filename/path, Cyrillic/brackets, explicit priority, empty/error legacy fallback, missing DLL rejection, isolated data and Closing");
        }
        finally
        {
            RutonyBotFunctions.RegisteredDirectory = null;
            RutonyBotFunctions.ThrowOnLookup = false;
            RutonyBotFunctions.Lookups.Clear();
            if (Directory.Exists(manual)) Directory.Delete(manual, true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
