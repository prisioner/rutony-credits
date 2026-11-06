using System.Net.WebSockets;
using System.Text.Json;

namespace RutonyCredits;

public sealed record TwitchRaidStatus(bool Available, bool Connected, string Phase, string? Error);

// Read-only consumer of Rutony's overlay stream. Never sends init or requests history.
public sealed class RutonyRaidSource(Action<string> record) : IAsyncDisposable
{
    private const int MaxMessageBytes = 65536;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task worker = Task.CompletedTask;
    private int port;
    private bool secure, disposed, ownsTwitch;
    private TwitchRaidStatus status = new(true, false, "Ожидание локального сервера Rutony", null);
    public TwitchRaidStatus Status => Volatile.Read(ref status);
    // Never switch ingress on a transient disconnect: a delayed overlay frame must
    // not be counted alongside a future host version's generic OnRaid callback.
    public bool OwnsTwitch => Volatile.Read(ref ownsTwitch);

    public async Task ConfigureAsync(int newPort, bool isSecure)
    {
        if (newPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(newPort));
        if (newPort == 0) isSecure = false;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || (newPort == port && isSecure == secure)) return;
            if (newPort > 0) Volatile.Write(ref ownsTwitch, true);
            await StopWorker().ConfigureAwait(false);
            port = newPort; secure = isSecure;
            if (port == 0)
            {
                Volatile.Write(ref status, new(true, false, "Локальный сервер Rutony недоступен", null));
                return;
            }
            var endpoint = new UriBuilder(secure ? "wss" : "ws", "127.0.0.1", port, "/AlertsList").Uri;
            cancellation = new CancellationTokenSource();
            worker = Run(endpoint, cancellation.Token);
        }
        finally { lifecycle.Release(); }
    }
    private async Task StopWorker()
    {
        if (cancellation == null) return;
        cancellation.Cancel();
        try { await worker.ConfigureAwait(false); }
        finally { cancellation.Dispose(); cancellation = null; worker = Task.CompletedTask; }
    }
    private async Task Run(Uri endpoint, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                using var abort = token.Register(socket.Abort);
                try
                {
                    Volatile.Write(ref status, new(true, false, "Подключение", null));
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(5));
                        await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref status, new(true, true, "Подключено к Rutony", null));
                    while (!token.IsCancellationRequested) await Read(socket, token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
                {
                    if (token.IsCancellationRequested) break;
                    Volatile.Write(ref status, new(true, false, "Переподключение", "Нет связи с локальным сервером Rutony. Рейды во время разрыва могут быть пропущены."));
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task Read(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096]; using var message = new MemoryStream();
        bool tooLarge = false; WebSocketMessageType? type = null;
        CancellationTokenSource? partial = null;
        try
        {
            while (true)
            {
                var chunk = await socket.ReceiveAsync(buffer.AsMemory(), partial?.Token ?? token).ConfigureAwait(false);
                if (chunk.MessageType == WebSocketMessageType.Close) throw new IOException("Rutony stream closed.");
                type ??= chunk.MessageType;
                if (type == WebSocketMessageType.Text && !tooLarge)
                {
                    if (message.Length + chunk.Count > MaxMessageBytes) tooLarge = true;
                    else message.Write(buffer, 0, chunk.Count);
                }
                if (chunk.EndOfMessage)
                {
                    if (!tooLarge && type == WebSocketMessageType.Text && !token.IsCancellationRequested)
                    {
                        var name = Name(message.GetBuffer().AsMemory(0, (int)message.Length));
                        if (name != null && !token.IsCancellationRequested) record(name);
                    }
                    return;
                }
                if (partial == null)
                {
                    partial = CancellationTokenSource.CreateLinkedTokenSource(token);
                    partial.CancelAfter(TimeSpan.FromSeconds(10));
                }
            }
        }
        finally { partial?.Dispose(); }
    }
    private static string? Name(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || type.GetString() != "Raid"
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("site", out var site) || site.ValueKind != JsonValueKind.String
                || !string.Equals(site.GetString(), "Twitch", StringComparison.OrdinalIgnoreCase)
                || !data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
            var text = name.GetString();
            return text != null && text.Length <= 200 && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text.Trim() : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await StopWorker().ConfigureAwait(false);
            Volatile.Write(ref status, new(true, false, "Остановлено", null));
        }
        finally { lifecycle.Release(); }
    }
}