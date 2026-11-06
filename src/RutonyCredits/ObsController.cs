using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace RutonyCredits;

public sealed record ObsOptions
{
    public bool Enabled { get; init; }
    public int Port { get; init; } = 4455;
    public string SceneName { get; init; } = "";
    public bool AutoStart { get; init; } = true;
    public bool ResetOnStop { get; init; } = true;
    public int ResetDelayMinutes { get; init; } = 5;
}
public sealed record ObsStatus(ObsOptions Options, bool Connected, string Phase, string? Error, bool HasPassword, string[] Scenes)
{
    public bool Available { get; init; } = true;
    public DateTimeOffset? PendingResetUntilUtc { get; init; }
}

public sealed class ObsController : IAsyncDisposable
{
    private readonly ObsSettingsStore storage;
    private readonly Func<Task> sceneEntered, sceneLeft, streamStarted;
    private readonly Func<Task<string>> captureSession;
    private readonly Func<string, CancellationToken, Task<bool>> resetSession;
    private readonly TimeProvider time;
    private sealed class PendingReset(string session, CancellationTokenSource cancellation)
    {
        public string Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
    private PendingReset? pendingReset;
    private readonly HashSet<PendingReset> resetJobs = [];
    private long resetGeneration;
    private readonly Action<string> warning;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task worker = Task.CompletedTask;
    private string? password;
    private ObsStatus status = new(new(), false, "Отключено", null, false, []);
    private bool disposed;
    public ObsController(string directory, Func<Task> sceneEntered, Func<Task> sceneLeft,
        Func<Task> streamStarted, Func<Task> streamStopped, Action<string> warning, TimeProvider? timeProvider = null)
        : this(directory, sceneEntered, sceneLeft, streamStarted, () => Task.FromResult(""),
            async (_, token) => { token.ThrowIfCancellationRequested(); await streamStopped(); return true; }, warning, timeProvider) { }

    public ObsController(string directory, Func<Task> sceneEntered, Func<Task> sceneLeft,
        Func<Task> streamStarted, Func<Task<string>> captureSession,
        Func<string, CancellationToken, Task<bool>> resetSession, Action<string> warning, TimeProvider? timeProvider = null)
    {
        storage = new(directory); this.sceneEntered = sceneEntered; this.sceneLeft = sceneLeft;
        this.streamStarted = streamStarted; this.captureSession = captureSession; this.resetSession = resetSession;
        this.warning = warning; time = timeProvider ?? TimeProvider.System;
        try
        {
            var saved = storage.Load(); password = saved.Password;
            status = new(saved.Options, false, saved.Options.Enabled ? "Подключаемся к OBS…" : "Отключено", null, !string.IsNullOrEmpty(password), []);
            StartWorker(saved.Options);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or CryptographicException or PlatformNotSupportedException)
        { status = status with { Error = "Не удалось прочитать настройки OBS или защищённый пароль. Сохраните настройки заново.", Phase = "Ошибка настроек OBS" }; }
    }
    public ObsStatus Status { get { lock (gate) return status with { Scenes = [.. status.Scenes] }; } }
    public static void Validate(ObsOptions options)
    {
        if (options.ResetDelayMinutes is < 1 or > 120) throw new ArgumentException("Задержка сброса OBS должна быть от 1 до 120 минут.");
        if (options.Port is < 1 or > 65535 || options.SceneName == null || options.SceneName.Length > 200 || options.SceneName.Any(char.IsControl))
            throw new ArgumentException("Проверьте порт и название сцены OBS.");
    }
    public async Task ConfigureAsync(ObsOptions options, string? password = null, bool clearPassword = false)
    {
        Validate(options);
        if (password?.Length > 4096) throw new ArgumentException("Пароль OBS слишком длинный.");
        if (!await lifecycle.WaitAsync(0)) throw new StoreOverloadedException();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            bool changePassword = clearPassword || !string.IsNullOrEmpty(password);
            string? nextPassword = clearPassword ? null : string.IsNullOrEmpty(password) ? this.password : password;
            try { nextPassword = storage.Save(options, nextPassword, changePassword); }
            catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException or InvalidDataException)
            { throw new IOException("Не удалось сохранить защищённый пароль OBS.", e); }
            await StopWorker(); this.password = nextPassword;
            lock (gate) status = new(options, false, options.Enabled ? "Подключаемся к OBS…" : "Отключено", null, !string.IsNullOrEmpty(nextPassword), []);
            StartWorker(options);
        }
        finally { lifecycle.Release(); }
    }
    private void StartWorker(ObsOptions options)
    {
        if (!options.Enabled) return;
        cancellation = new(); var token = cancellation.Token; string? savedPassword = password;
        worker = Task.Run(() => Work(options, savedPassword, token));
    }
    private async Task StopWorker()
    {
        if (cancellation == null) return;
        cancellation.Cancel();
        CancelPendingReset();
        try { await worker; } catch (OperationCanceledException) { }
        cancellation.Dispose(); cancellation = null;
    }
    private long CancelPendingReset()
    {
        lock (gate)
        {
            // Return this invalidation's generation, even if cancellation callbacks
            // re-enter and invalidate it again before Cancel returns.
            long generation = ++resetGeneration;
            pendingReset?.Cancellation.Cancel();
            pendingReset = null;
            status = status with { PendingResetUntilUtc = null };
            return generation;
        }
    }
    private async Task DrainResets()
    {
        Task[] tasks;
        lock (gate) tasks = resetJobs.Select(job => job.Task).ToArray();
        await Task.WhenAll(tasks);
    }
    // Manual reset cancels the timer and drains a reset command already in the store queue.
    public async Task CancelPendingResetAsync()
    {
        if (!await lifecycle.WaitAsync(0)) throw new StoreOverloadedException();
        try { CancelPendingReset(); await DrainResets(); }
        finally { lifecycle.Release(); }
    }
    private async Task ScheduleReset(ObsOptions options, Func<CancellationToken, Task<JsonElement>> query, CancellationToken token)
    {
        long generation = CancelPendingReset();
        string session;
        try { session = await captureSession(); }
        catch
        {
            if (!token.IsCancellationRequested) Warn("Не удалось подготовить сброс OBS. Статистика сохранена; проверьте текущий период.");
            return;
        }
        lock (gate)
        {
            if (token.IsCancellationRequested || generation != resetGeneration) return;
            var job = new PendingReset(session, CancellationTokenSource.CreateLinkedTokenSource(token));
            pendingReset = job; resetJobs.Add(job);
            status = status with { PendingResetUntilUtc = time.GetUtcNow().AddMinutes(options.ResetDelayMinutes) };
            // Do not await this task in the event handler: the receiver must remain
            // free to dispatch the op7 response to the fresh GetStreamStatus request.
            job.Task = DelayedReset(job, options, query);
        }
    }
    private async Task DelayedReset(PendingReset job, ObsOptions options, Func<CancellationToken, Task<JsonElement>> query)
    {
        var token = job.Cancellation.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(options.ResetDelayMinutes), time, token);
            var current = await query(token);
            token.ThrowIfCancellationRequested();
            if (current.GetProperty("outputActive").GetBoolean() || current.GetProperty("outputReconnecting").GetBoolean())
            {
                ClearPendingReset(job);
                Warn("OBS продолжает передачу или переподключается. Отложенный сброс отменён; статистика сохранена.");
                return;
            }
            // The callback must check both session and token INSIDE the store queue.
            // Always await that barrier, even after cancellation or reconfiguration.
            await resetSession(job.Session, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                ClearPendingReset(job);
                const string message = "Не удалось подтвердить остановку OBS или выполнить сброс. Статистика сохранена; проверьте текущий период.";
                lock (gate) status = status with { Error = message };
                Warn(message);
            }
        }
        finally
        {
            ClearPendingReset(job);
            lock (gate) resetJobs.Remove(job);
            job.Cancellation.Dispose();
        }
    }
    private void ClearPendingReset(PendingReset job)
    {
        lock (gate)
        {
            if (ReferenceEquals(pendingReset, job))
            { pendingReset = null; status = status with { PendingResetUntilUtc = null }; }
        }
    }
    private void Update(bool connected, string phase, string? error = null, string[]? scenes = null)
    { lock (gate) status = status with { Connected = connected, Phase = phase, Error = error, Scenes = scenes ?? status.Scenes }; }
    private void Warn(string message) { try { warning(message); } catch { /* Notification must not break the receiver. */ } }
    private async Task Invoke(Func<Task> callback, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Drain a started store barrier before acknowledging reconfiguration.
        // Cancelling only the wait would leave a late Run/Reset after disable.
        try { await callback(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            Update(true, "Подключено к OBS", "Не удалось выполнить команду титров от OBS. Проверьте панель.");
            Warn("Не удалось выполнить команду титров от OBS. Проверьте панель; статистика не удаляется при ошибке.");
        }
    }
    private async Task Work(ObsOptions options, string? savedPassword, CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            string? program = null, stream = null;
            bool streamWasActive = false, stopping = false;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            Func<CancellationToken, Task<JsonElement>>? query = null;
            try
            {
                Update(false, "Подключаемся к OBS…");
                await ObsProtocol.ListenAsync(options, savedPassword, (baseline, request) =>
                {
                    query = request;
                    program = baseline.Scene; stream = baseline.StreamState;
                    streamWasActive = stream is "OBS_WEBSOCKET_OUTPUT_STARTED" or "OBS_WEBSOCKET_OUTPUT_RECONNECTED" or "OBS_WEBSOCKET_OUTPUT_RECONNECTING";
                    stopping = false; attempt = 0;
                    Update(true, options.AutoStart && options.SceneName.Length == 0 ? "Выберите сцену титров" : "Подключено к OBS", scenes: baseline.Scenes);
                }, async (type, data) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (type == "SceneListChanged") { lock (gate) status = status with { Scenes = ObsProtocol.Scenes(data) }; return; }
                    if (type is "CurrentSceneCollectionChanged" or "SceneNameChanged") throw new ObsFault("Список сцен OBS изменился; обновляем подключение.");
                    if (type == "CurrentProgramSceneChanged")
                    {
                        var next = ObsProtocol.Text(data, "sceneName", 200);
                        if (next == program) return;
                        var previous = program; program = next;
                        if (!options.AutoStart || options.SceneName.Length == 0) return;
                        if (next == options.SceneName) await Invoke(sceneEntered, token);
                        else if (previous == options.SceneName) await Invoke(sceneLeft, token);
                    }
                    else if (type == "StreamStateChanged")
                    {
                        var next = ObsProtocol.Text(data, "outputState", 100);
                        if (next == stream) return;
                        stream = next;
                        switch (next)
                        {
                            case "OBS_WEBSOCKET_OUTPUT_STARTING": CancelPendingReset(); stopping = false; streamWasActive = false; break;
                            case "OBS_WEBSOCKET_OUTPUT_STARTED":
                                CancelPendingReset(); stopping = false; streamWasActive = true; await Invoke(streamStarted, token); break;
                            case "OBS_WEBSOCKET_OUTPUT_RECONNECTING":
                            case "OBS_WEBSOCKET_OUTPUT_RECONNECTED": CancelPendingReset(); stopping = false; streamWasActive = true; break;
                            case "OBS_WEBSOCKET_OUTPUT_STOPPING": stopping = streamWasActive; break;
                            case "OBS_WEBSOCKET_OUTPUT_STOPPED":
                                bool normalStop = stopping && streamWasActive;
                                bool unexpectedStop = !stopping && streamWasActive;
                                stopping = false; streamWasActive = false;
                                if (normalStop && options.ResetOnStop) await ScheduleReset(options, query!, connection.Token);
                                else if (unexpectedStop) Warn("OBS завершил передачу без подтверждённой остановки. Статистика сохранена; проверьте текущий период.");
                                break;
                            default: CancelPendingReset(); stopping = false; break;
                        }
                    }
                }, connection.Token, time);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) when (e is ObsFault or WebSocketException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                string message = e is ObsFault ? e.Message : "Не удалось связаться с OBS. Проверьте WebSocket-сервер и порт; статистика сохранена.";
                Update(false, "Нет связи с OBS", message);
            }
            finally
            {
                connection.Cancel();
                CancelPendingReset();
                await DrainResets();
            }
            // A new connection gets a new baseline and can never finish an old stop.
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, 1 << Math.Min(attempt++, 2))), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync(false);

    // Only a successfully started RuntimeHost uses this close path. Capture the
    // pending period before our own cancellation clears it; never infer a stop
    // merely from disconnecting. The owner applies the returned session guard
    // after the receiver and all timer/store barriers have drained.
    internal ValueTask<string?> CloseAsync() => StopAsync(true);

    private async ValueTask<string?> StopAsync(bool finishPendingReset)
    {
        await lifecycle.WaitAsync();
        try
        {
            string? session;
            lock (gate)
            {
                if (disposed) return null;
                disposed = true;
                session = finishPendingReset && status.Connected && pendingReset is { } job && !job.Cancellation.IsCancellationRequested
                    ? job.Session : null;
            }
            await StopWorker();
            lock (gate) status = status with { Connected = false, Phase = "Отключено" };
            return session;
        }
        finally { lifecycle.Release(); }
    }
}
