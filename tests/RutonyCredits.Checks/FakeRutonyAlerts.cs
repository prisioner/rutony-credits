using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

internal sealed class FakeRutonyAlerts : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<Peer> peers = Channel.CreateUnbounded<Peer>();
    private readonly ConcurrentBag<WebSocket> sockets = [];
    private int handshakes;
    public int Port { get; private set; }
    public int Handshakes => Volatile.Read(ref handshakes);
    private FakeRutonyAlerts(WebApplication app) { this.app = app; }
    public static async Task<FakeRutonyAlerts> Start(bool holdHandshake = false)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(FakeRutonyAlerts).Assembly.GetName().Name });
        builder.Logging.ClearProviders(); builder.Services.AddRouting();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build(); var server = new FakeRutonyAlerts(app);
        app.UseWebSockets();
        app.Map("/AlertsList", async context =>
        {
            Interlocked.Increment(ref server.handshakes);
            if (holdHandshake)
            {
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { }
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            server.sockets.Add(socket); var peer = new Peer(socket);
            server.peers.Writer.TryWrite(peer);
            var buffer = new byte[4096];
            try
            {
                while (!server.stop.IsCancellationRequested)
                {
                    var message = await socket.ReceiveAsync(buffer.AsMemory(), server.stop.Token);
                    if (message.MessageType == WebSocketMessageType.Close) break;
                    Interlocked.Increment(ref peer.ApplicationFrames);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        });
        await app.StartAsync(); server.Port = new Uri(app.Urls.Single()).Port; return server;
    }
    public Task<Peer> Next() => peers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); foreach (var socket in sockets) socket.Abort();
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); await app.DisposeAsync(); stop.Dispose();
    }
    internal sealed class Peer(WebSocket socket)
    {
        public int ApplicationFrames;
        public async Task Send(string text, int split = 0, bool binary = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var type = binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
            if (split > 0 && split < bytes.Length)
            {
                await socket.SendAsync(bytes.AsMemory(0, split), type, false, CancellationToken.None);
                await socket.SendAsync(bytes.AsMemory(split), type, true, CancellationToken.None);
            }
            else await socket.SendAsync(bytes.AsMemory(), type, true, CancellationToken.None);
        }
        public Task Partial(string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, false, CancellationToken.None).AsTask();
        public void Drop() => socket.Abort();
    }
}