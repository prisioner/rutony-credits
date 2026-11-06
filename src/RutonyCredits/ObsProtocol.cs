using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RutonyCredits;

internal sealed class ObsFault(string message) : Exception(message);
internal sealed record ObsBaseline(string Scene, string StreamState, string[] Scenes);

// One receiver owns handshake, baseline and events. Initial/reconnect observations
// are never replayed as scene or stream transitions. No writes to OBS are made.
internal static class ObsProtocol
{
    private const int MaximumMessage = 1024 * 1024;
    public static async Task ListenAsync(ObsOptions options, string? password, Action<ObsBaseline, Func<CancellationToken, Task<JsonElement>>> ready,
        Func<string, JsonElement, Task> changed, CancellationToken token, TimeProvider? timeProvider = null)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        using var abort = token.Register(socket.Abort);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            try { await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{options.Port}"), connect.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ObsFault("OBS не ответил. Проверьте порт и WebSocket-сервер."); }
        }
        using var hello = await TimedRead(socket, token);
        if (Int(hello.RootElement, "op") != 0) throw new ObsFault("Ожидается OBS WebSocket версии 5.");
        var greeting = hello.RootElement.GetProperty("d");
        if (Int(greeting, "rpcVersion") < 1) throw new ObsFault("Версия WebSocket OBS не поддерживается.");
        var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 2 | 4 | 64 };
        if (greeting.TryGetProperty("authentication", out var authentication))
        {
            if (string.IsNullOrEmpty(password)) throw new ObsFault("Для подключения к OBS введите пароль WebSocket.");
            var salt = Text(authentication, "salt", 4096); var challenge = Text(authentication, "challenge", 4096);
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
            identify["authentication"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }
        await Send(socket, new { op = 1, d = identify }, token);
        using (var identified = await TimedRead(socket, token))
        {
            if (Int(identified.RootElement, "op") != 2 || Int(identified.RootElement.GetProperty("d"), "negotiatedRpcVersion") != 1)
                throw new ObsFault("Не удалось согласовать подключение к OBS.");
        }
        string? observedScene = null, observedStream = null; string[]? observedScenes = null;
        void Observe(string type, JsonElement data)
        {
            if (type == "CurrentProgramSceneChanged") observedScene = Text(data, "sceneName", 200);
            else if (type == "StreamStateChanged") observedStream = Text(data, "outputState", 100);
            else if (type == "SceneListChanged") observedScenes = Scenes(data);
        }
        async Task<JsonElement> Request(string type)
        {
            string id = Guid.NewGuid().ToString("N"); await Send(socket, new { op = 6, d = new { requestType = type, requestId = id } }, token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    using var message = await Read(socket, deadline.Token); var root = message.RootElement; int opcode = Int(root, "op"); var data = root.GetProperty("d");
                    if (opcode == 5) { Observe(Text(data, "eventType", 200), data.GetProperty("eventData")); continue; }
                    if (opcode != 7 || Text(data, "requestId", 100) != id || Text(data, "requestType", 100) != type) throw new ObsFault("Некорректный ответ OBS.");
                    if (!data.GetProperty("requestStatus").GetProperty("result").GetBoolean()) throw new ObsFault("OBS не предоставил состояние. Проверьте версию WebSocket.");
                    return data.GetProperty("responseData").Clone();
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ObsFault("OBS не ответил на запрос состояния."); }
        }
        var sceneList = await Request("GetSceneList"); var scene = await Request("GetCurrentProgramScene"); var stream = await Request("GetStreamStatus");
        var initialState = stream.TryGetProperty("outputReconnecting", out var reconnecting) && reconnecting.GetBoolean() ? "OBS_WEBSOCKET_OUTPUT_RECONNECTING"
            : stream.GetProperty("outputActive").GetBoolean() ? "OBS_WEBSOCKET_OUTPUT_STARTED" : "OBS_WEBSOCKET_OUTPUT_STOPPED";
        var pending = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();
        using var sends = new SemaphoreSlim(1, 1);
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<JsonElement> QueryStream(CancellationToken cancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, requests.Token);
            var queryToken = linked.Token;
            string id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                await sends.WaitAsync(queryToken);
                try { await Send(socket, new { op = 6, d = new { requestType = "GetStreamStatus", requestId = id } }, queryToken); }
                finally { sends.Release(); }
                try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), timeProvider ?? TimeProvider.System, queryToken); }
                catch (TimeoutException) { throw new ObsFault("OBS не ответил на запрос состояния."); }
            }
            finally { pending.TryRemove(id, out _); }
        }
        try
        {
            ready(new(observedScene ?? Text(scene, "currentProgramSceneName", 200), observedStream ?? initialState, observedScenes ?? Scenes(sceneList)), QueryStream);
            while (true)
            {
                using var message = await Read(socket, token); var root = message.RootElement;
                int opcode = Int(root, "op"); var data = root.GetProperty("d");
                if (opcode == 5)
                    await changed(Text(data, "eventType", 200), data.GetProperty("eventData"));
                else if (opcode == 7)
                {
                    // A timed-out/cancelled request may still receive a late response.
                    if (!pending.TryRemove(Text(data, "requestId", 100), out var completion)) continue;
                    if (Text(data, "requestType", 100) != "GetStreamStatus"
                        || !data.GetProperty("requestStatus").GetProperty("result").GetBoolean())
                        completion.TrySetException(new ObsFault("OBS не предоставил состояние. Проверьте версию WebSocket."));
                    else completion.TrySetResult(data.GetProperty("responseData").Clone());
                }
                else throw new ObsFault("Неожиданное сообщение OBS.");
            }
        }
        finally
        {
            requests.Cancel();
            foreach (var completion in pending.Values) completion.TrySetCanceled(requests.Token);
        }
    }

    internal static string Text(JsonElement element, string key, int maximum)
    {
        var value = element.GetProperty(key).GetString();
        if (value == null || value.Length > maximum || value.Any(char.IsControl)) throw new ObsFault("Некорректные данные OBS.");
        return value;
    }
    private static int Int(JsonElement element, string key) => element.GetProperty(key).GetInt32();
    internal static string[] Scenes(JsonElement data)
    {
        var scenes = data.GetProperty("scenes");
        if (scenes.ValueKind != JsonValueKind.Array || scenes.GetArrayLength() > 5000) throw new ObsFault("Некорректный список сцен OBS.");
        return scenes.EnumerateArray().Select(scene => Text(scene, "sceneName", 200)).Distinct(StringComparer.Ordinal).ToArray();
    }
    private static async Task Send(ClientWebSocket socket, object data, CancellationToken token) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(data).AsMemory(), WebSocketMessageType.Text, true, token);
    private static async Task<JsonDocument> TimedRead(ClientWebSocket socket, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await Read(socket, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ObsFault("OBS не завершил подключение вовремя."); }
    }
    private static async Task<JsonDocument> Read(ClientWebSocket socket, CancellationToken token)
    {
        using var memory = new MemoryStream(); var buffer = new byte[8192]; ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new ObsFault((int?)socket.CloseStatus == 4009 ? "Не удалось войти в OBS. Проверьте пароль WebSocket." : "Связь с OBS прервана; статистика сохранена.");
            if (received.MessageType != WebSocketMessageType.Text || memory.Length + received.Count > MaximumMessage) throw new ObsFault("Некорректное сообщение OBS.");
            memory.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}
