using RutonyCredits;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "web");
var data = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Environment.CurrentDirectory, ".runtime", "preview");
var port = args.Length > 2 ? int.Parse(args[2]) : 18765;
using var runtime = RuntimeHost.StartPreview(Path.GetDirectoryName(root)!, data, port);
Console.WriteLine("Ручной стенд без RutonyChat. Реальные события не подключены.");
Console.WriteLine("Управление: " + runtime.ControlUrl);
Console.WriteLine("OBS: " + runtime.OverlayUrl);
var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };
await stopped.Task;
