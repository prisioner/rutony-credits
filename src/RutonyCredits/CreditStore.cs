using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RutonyCredits;

// All disk access and mutable state belong to one consumer. Commands form barriers
// in the same queue as incoming events, including reset and playback snapshots.
public sealed class CreditStore : IAsyncDisposable
{
    private readonly string directory;
    private readonly FileStream lease;
    private const int RecordCapacity = 10000, CommandCapacity = 256;
    // Loss markers: <= pending resets + 1. Context safety markers:
    // <= pending context commands + 2 platforms. One terminal Close action.
    // Separate reserves keep these ordered even when both admission budgets fill.
    private readonly Channel<Action> queue = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(RecordCapacity + 3 * CommandCapacity + 4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private readonly List<CreditEvent> events = [];
    private readonly HashSet<string> sourceIds = [];
    private sealed record ChannelOwner(string? Name, string? EventId, string? Context = null);
    private readonly Dictionary<string, ChannelOwner> channelOwners = [];
    private readonly Dictionary<string, string?> channelContexts = [];
    // Admission metadata belongs to gate; effective identities belong to worker.
    private readonly Dictionary<string, long> contextEpochs = [], invalidatedContextEpochs = [];
    private string sessionId = "";
    private long sequence;
    private CreditSettings settings = new();
    private CreditRun? run;
    private string playback = "idle";
    private enum FaultKind { Journal, Settings }
    private readonly Dictionary<FaultKind, string> faults = [];
    private long ingressEpoch;
    private long overflowEpoch = -1;
    private int pending, pendingCommands;
    private readonly object gate = new();
    private bool disposed;
    private Task? closing;
    private static readonly string[] SectionKeys = ["vk-raids", "twitch-raids", "donations", "followers", "subscribers", "moderators"];

    public CreditStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        // Prevent two copies of the script from collecting into the same period.
        lease = new(Path.Combine(this.directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { Restore(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { faults[FaultKind.Journal] = "Не удалось восстановить журнал. Сбросьте счётчики; прежние файлы сохранены."; }
        try
        {
            var config = Path.Combine(this.directory, "settings.json");
            if (File.Exists(config)) settings = Validate(JsonSerializer.Deserialize<CreditSettings>(BoundedFiles.Text(config, BoundedFiles.SettingsBytes), Wire.Json) ?? throw new InvalidDataException());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { faults[FaultKind.Settings] = "Не удалось прочитать оформление. Сохраните настройки оформления заново; исходный файл будет сохранён."; }
        RestoreOwners();
        worker = Task.Run(async () => { await foreach (var action in queue.Reader.ReadAllAsync()) action(); });
    }

    public bool Record(EventInput input)
    {
        if (input.IsTest || !Valid(input)) return false;
        lock (gate)
        {
            if (disposed) return false;
            if (pending >= RecordCapacity)
            {
                // Faults follow the same reset barriers as records. One marker per
                // enqueue epoch bounds memory without losing faults across resets.
                if (overflowEpoch != ingressEpoch)
                {
                    overflowEpoch = ingressEpoch;
                    queue.Writer.TryWrite(() =>
                    {
                        faults[FaultKind.Journal] = "Очередь событий переполнена. Текущая статистика неполна; сбросьте счётчики.";
                        MarkIncomplete();
                    });
                }
                return false;
            }
            pending++;
            var received = DateTimeOffset.UtcNow;
            queue.Writer.TryWrite(() =>
            {
                try { Append(input, received); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { faults[FaultKind.Journal] = "Ошибка записи журнала. Показ реальных данных заблокирован; устраните причину и сбросьте счётчики."; MarkIncomplete(); }
                finally { lock (gate) pending--; }
            });
            return true;
        }
    }

    // Legacy callers can provide a live owner, but an unscoped observation is
    // never trusted after restart. New callers publish context before identity.
    public bool ObserveChannelOwner(string platform, string? displayName, string? sourceEventId) =>
        ObserveOwner(platform, null, displayName, sourceEventId, false);

    public bool ObserveChannelContext(string platform, string? context)
    {
        if (platform is not ("twitch" or "vkvideolive") || !ValidContext(context)) return false;
        lock (gate)
        {
            if (disposed) return false;
            var epoch = contextEpochs.GetValueOrDefault(platform);
            if (pendingCommands >= CommandCapacity)
            {
                // Losing a context update must never keep a foreign owner active.
                // Coalesce only if no admitted context update separates the markers.
                if (!invalidatedContextEpochs.TryGetValue(platform, out var previous) || previous != epoch)
                {
                    invalidatedContextEpochs[platform] = epoch;
                    queue.Writer.TryWrite(() => ApplyContext(platform, null));
                }
                return false;
            }
            contextEpochs[platform] = epoch + 1;
            return AdmitOwnerUpdate(() => ApplyContext(platform, context));
        }
    }

    private void ApplyContext(string platform, string? context)
    {
        channelContexts[platform] = context;
        if (channelOwners.TryGetValue(platform, out var owner) && owner.Context == null)
            channelOwners.Remove(platform);
    }

    public bool ObserveChannelOwnerForContext(string platform, string context, string? displayName, string? sourceEventId) =>
        ObserveOwner(platform, context, displayName, sourceEventId, true);

    private bool ObserveOwner(string platform, string? context, string? displayName, string? sourceEventId, bool scoped)
    {
        if (platform is not ("twitch" or "vkvideolive") || !ValidContext(context) || (scoped && context == null)) return false;
        var name = displayName == null ? null : ModeratorName(displayName);
        if (name?.Length > 200 || (name != null && name.Any(char.IsControl))) name = null;
        if (sourceEventId?.Length > 200 || (sourceEventId != null && sourceEventId.Any(char.IsControl))) sourceEventId = null;
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(sourceEventId)) return false;
        return AdmitOwnerUpdate(() =>
        {
            if (scoped && channelContexts.GetValueOrDefault(platform) != context) return;
            channelOwners[platform] = new(name, sourceEventId, scoped ? context : channelContexts.GetValueOrDefault(platform));
            SaveOwners();
        });
    }

    private static bool ValidContext(string? context) => context == null
        || (!string.IsNullOrWhiteSpace(context) && context.Length <= 2048 && !context.Any(char.IsControl));

    private bool AdmitOwnerUpdate(Action update)
    {
        lock (gate)
        {
            if (disposed || pendingCommands >= CommandCapacity) return false;
            pendingCommands++;
            return queue.Writer.TryWrite(() =>
            {
                try { update(); }
                finally { lock (gate) pendingCommands--; }
            });
        }
    }

    private string OwnersPath => Path.Combine(directory, "sessions", sessionId + ".owners.json");
    private void SaveOwners()
    {
        if (channelOwners.Count == 0 || !Guid.TryParseExact(sessionId, "N", out _)) return;
        try { AtomicWrite(OwnersPath, channelOwners.Where(x => x.Value.Context != null).ToDictionary()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Derived identity cache must never stop the primary journal. */ }
    }
    private void RestoreOwners()
    {
        try
        {
            if (!File.Exists(OwnersPath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, ChannelOwner>>(BoundedFiles.Text(OwnersPath, BoundedFiles.CacheBytes), Wire.Json);
            if (saved == null) return;
            foreach (var (platform, owner) in saved)
            {
                if (platform is not ("twitch" or "vkvideolive") || owner == null || owner.Context == null || !ValidContext(owner.Context)) continue;
                var name = owner.Name == null ? null : ModeratorName(owner.Name);
                if (name?.Length > 200 || (name != null && name.Any(char.IsControl)) || owner.EventId?.Length > 200
                    || (owner.EventId != null && owner.EventId.Any(char.IsControl))) continue;
                if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(owner.EventId)) channelOwners[platform] = new(name, owner.EventId, owner.Context);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        { /* Optional cache can be reacquired through Rutony; real events stay valid. */ }
    }

    private static string ModeratorName(string name)
    {
        name = name.Trim();
        return name.StartsWith('@') ? name[1..].Trim() : name;
    }

    private static bool IsExcludedModerator(string kind, string displayName, CreditSettings config) =>
        kind == "moderator" && config.ModeratorExclusions.Contains(ModeratorName(displayName), StringComparer.OrdinalIgnoreCase);
    private IEnumerable<CreditEvent> VisibleModerators(IEnumerable<CreditEvent> source, CreditSettings config)
    {
        var excluded = new HashSet<string>(config.ModeratorExclusions, StringComparer.OrdinalIgnoreCase);
        return source.Where(e => !excluded.Contains(ModeratorName(e.DisplayName))
            && (!channelOwners.TryGetValue(e.Platform, out var owner)
                || owner.Context != channelContexts.GetValueOrDefault(e.Platform)
                || (!string.Equals(owner.Name, ModeratorName(e.DisplayName), StringComparison.OrdinalIgnoreCase)
                    && (owner.EventId == null || owner.EventId != e.SourceEventId))));
    }

    private static bool Valid(EventInput x) => x.Kind is "raid" or "donation" or "follower" or "subscriber" or "moderator"
        && (x.Kind == "donation" || x.Platform is "twitch" or "vkvideolive")
        && !string.IsNullOrWhiteSpace(x.DisplayName) && x.DisplayName.Length <= 200
        && !x.DisplayName.Any(char.IsControl) && !string.IsNullOrEmpty(x.Source) && x.Source.Length <= 80
        && !string.IsNullOrWhiteSpace(x.Platform) && x.Platform.Length <= 80 && (x.SourceEventId == null || x.SourceEventId.Length <= 200)
        && ((x.Amount == null && x.Currency == null) || (x.Kind == "donation" && x.Amount is >= 0 && ValidCurrency(x.Currency)));

    internal static bool ValidCurrency(string? value) => value != null
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9]{1,12}$");

    private static string Display(CreditEvent item) => item.Amount.HasValue && item.Currency != null
        ? item.DisplayName + " — " + item.Amount.Value.ToString("0.00##########################", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " " + item.Currency.ToUpperInvariant()
        : item.DisplayName;

    private void Append(EventInput x, DateTimeOffset received)
    {
        // Worker settings follow the same FIFO barriers as incoming events.
        if (faults.ContainsKey(FaultKind.Journal) || IsExcludedModerator(x.Kind, x.DisplayName, settings)) return;
        var key = DedupKey(x.Source, x.Platform, x.Kind, x.SourceEventId);
        if (key != null && sourceIds.Contains(key)) return;
        var item = new CreditEvent(sessionId, Guid.NewGuid().ToString("N"), sequence + 1,
            x.Source, x.Platform, x.Kind, x.DisplayName.Trim(), received, x.OccurredAtUtc, x.SourceEventId, x.Amount, x.Currency?.ToUpperInvariant());
        // Open/flush/close each record: an acknowledged barrier means durable writes.
        using (var file = new FileStream(Journal(sessionId), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, Wire.Json) + "\n");
            file.Write(bytes); file.Flush(true);
        }
        events.Add(item); sequence++;
        if (key != null) sourceIds.Add(key);
    }

    private string? Problem => faults.Count == 0 ? null : string.Join(" ", faults.Values);
    private string Journal(string id) => Path.Combine(directory, "sessions", id + ".jsonl");
    private void MarkIncomplete()
    {
        try { using var file = File.Create(Path.Combine(directory, "sessions", sessionId + ".incomplete")); file.Flush(true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* A failed disk cannot guarantee its own fault marker. */ }
    }
    private static string? DedupKey(string source, string platform, string kind, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : JsonSerializer.Serialize(new[] { source, platform, kind, id });

    private void Restore()
    {
        Directory.CreateDirectory(Path.Combine(directory, "sessions"));
        var manifest = Path.Combine(directory, "current.json");
        if (!File.Exists(manifest))
        {
            if (Directory.EnumerateFileSystemEntries(Path.Combine(directory, "sessions")).Any()) throw new InvalidDataException();
            NewSession();
        }
        else
        {
            var id = JsonSerializer.Deserialize<string>(BoundedFiles.Text(manifest, BoundedFiles.ManifestBytes), Wire.Json);
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException();
            sessionId = id;
            if (!File.Exists(Journal(id))) throw new InvalidDataException();
            if (File.Exists(Path.Combine(directory, "sessions", id + ".incomplete")))
                faults[FaultKind.Journal] = "Прежний сбор событий завершился с ошибкой. Сбросьте счётчики; старый журнал сохранён.";
            foreach (var line in BoundedFiles.Lines(Journal(id)))
            {
                var e = JsonSerializer.Deserialize<CreditEvent>(line, Wire.Json) ?? throw new InvalidDataException();
                if (e.SessionId != id || e.Sequence != sequence + 1 || !Valid(new(e.Source, e.Platform, e.Kind, e.DisplayName, e.SourceEventId, Amount: e.Amount, Currency: e.Currency)))
                    throw new InvalidDataException();
                events.Add(e); sequence = e.Sequence;
                var key = DedupKey(e.Source, e.Platform, e.Kind, e.SourceEventId);
                if (key != null && !sourceIds.Add(key)) throw new InvalidDataException();
            }
        }
    }

    private void NewSession()
    {
        var next = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(directory, "sessions"));
        using (File.Create(Journal(next))) { }
        var manifest = Path.Combine(directory, "current.json");
        if (faults.ContainsKey(FaultKind.Journal)) BoundedFiles.Preserve(manifest);
        AtomicWrite(manifest, next);
        sessionId = next; sequence = 0; events.Clear(); sourceIds.Clear();
        faults.Remove(FaultKind.Journal); run = null; playback = "idle";
        SaveOwners();
    }

    private static void AtomicWrite<T>(string target, T data)
    {
        var temp = target + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, data, Wire.Json); file.Flush(true); }
        File.Move(temp, target, true);
    }

    private Task<T> Dispatch<T>(Func<T> operation, bool resetBarrier = false)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(CreditStore)));
            if (pendingCommands >= CommandCapacity) return Task.FromException<T>(new StoreOverloadedException());
            pendingCommands++;
            if (resetBarrier) ingressEpoch++;
            queue.Writer.TryWrite(() =>
            {
                try { result.SetResult(operation()); }
                catch (Exception e) { result.SetException(e); }
                finally { lock (gate) pendingCommands--; }
            });
        }
        return result.Task;
    }

    public Task<CreditState> StateAsync() => Dispatch(() => new CreditState(sessionId,
        events.Count(e => !IsExcludedModerator(e.Kind, e.DisplayName, settings)),
        events.GroupBy(Section).ToDictionary(x => x.Key, x => x.Key == "moderators"
            ? VisibleModerators(x, settings).Select(e => e.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() : x.Count()), Problem, playback,
        run?.RunId, run?.Mode, Copy(settings), DateTimeOffset.UtcNow));

    private void CheckSession(string expected)
    { if (expected != sessionId) throw new InvalidOperationException("Период изменился. Обновите страницу управления."); }

    public Task<string> ResetAsync(string expected) => Dispatch(() => { CheckSession(expected); NewSession(); return sessionId; }, resetBarrier: true);
    public Task<bool> ResetIfCurrentAsync(string expected, CancellationToken permission) => Dispatch(() =>
    {
        if (permission.IsCancellationRequested || sessionId != expected) return false;
        NewSession(); return true;
    }, resetBarrier: true);
    public Task StopAsync(string expected, string? expectedRunId) => Dispatch(() =>
    {
        CheckSession(expected);
        if (expectedRunId != run?.RunId) throw new InvalidOperationException("Показ изменился. Обновите страницу управления.");
        run = null; playback = "idle"; return true;
    });
    public Task SetSettingsAsync(CreditSettings value, string expected) => Dispatch(() =>
    {
        CheckSession(expected); var valid = Validate(value);
        var path = Path.Combine(directory, "settings.json");
        if (faults.ContainsKey(FaultKind.Settings)) BoundedFiles.Preserve(path);
        AtomicWrite(path, valid); settings = valid; faults.Remove(FaultKind.Settings); return true;
    });

    public static CreditSettings Validate(CreditSettings value)
    {
        // Only the new section may be absent in legacy settings. Never mutate the caller.
        if (value.Headings != null && !value.Headings.ContainsKey("moderators"))
            value = value with { Headings = new(value.Headings) { ["moderators"] = "Модераторы" } };
        if (value.EnabledSections != null && !value.EnabledSections.ContainsKey("moderators"))
            value = value with { EnabledSections = new(value.EnabledSections) { ["moderators"] = true } };
        if (value.ModeratorExclusions == null || value.ModeratorExclusions.Length > 50
            || value.ModeratorExclusions.Any(n => n == null || ModeratorName(n).Length is < 1 or > 200 || n.Any(char.IsControl)))
            throw new ArgumentException("Проверьте список исключений модераторов: до 50 ников, до 200 символов в каждом.");
        value = value with { ModeratorExclusions = value.ModeratorExclusions.Select(ModeratorName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        if (value.FontFamily == null || value.FontFamily.Length is < 1 or > 100 || value.FontFamily.Any(char.IsControl)
            || value.FontSize is < 16 or > 160 || value.Speed is < 10 or > 400
            || value.TextColor == null || !System.Text.RegularExpressions.Regex.IsMatch(value.TextColor, "^#[0-9a-fA-F]{6}$")
            || value.EnabledSections == null || value.EnabledSections.Count != SectionKeys.Length
            || SectionKeys.Any(k => !value.EnabledSections.ContainsKey(k))
            || value.Headings == null || value.Headings.Count != SectionKeys.Length
            || SectionKeys.Any(k => !value.Headings.TryGetValue(k, out var h) || string.IsNullOrWhiteSpace(h) || h.Length > 120 || h.Any(char.IsControl)))
            throw new ArgumentException("Недопустимые настройки оформления.");
        return Copy(value);
    }

    private static CreditSettings Copy(CreditSettings x) => x with { Headings = new(x.Headings), EnabledSections = new(x.EnabledSections), ModeratorExclusions = [.. x.ModeratorExclusions] };
    private static string Section(CreditEvent e) => e.Kind switch
    {
        "raid" => e.Platform == "vkvideolive" ? "vk-raids" : "twitch-raids",
        "donation" => "donations", "follower" => "followers", "moderator" => "moderators", _ => "subscribers"
    };

    private string[] Names(string key, IEnumerable<CreditEvent> source, CreditSettings config)
    {
        if (key == "moderators") source = VisibleModerators(source, config);
        var ordered = source.OrderBy(e => e.OccurredAtUtc ?? e.ReceivedAtUtc).ThenBy(e => e.Sequence).ToArray();
        if (key == "moderators") return ordered.Select(e => e.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (key != "donations" || !config.AggregateDonations) return ordered.Select(Display).ToArray();
        var totals = new Dictionary<(string Name, string Currency), decimal>();
        var overflow = new HashSet<(string Name, string Currency)>();
        foreach (var e in ordered.Where(e => e.Amount.HasValue && e.Currency != null))
        {
            var group = (e.DisplayName.ToUpperInvariant(), e.Currency!.ToUpperInvariant());
            if (overflow.Contains(group)) continue;
            try { totals[group] = checked(totals.GetValueOrDefault(group) + e.Amount!.Value); }
            catch (OverflowException) { overflow.Add(group); }
        }
        var emitted = new HashSet<(string Name, string Currency)>();
        var result = new List<string>();
        foreach (var e in ordered)
        {
            if (!e.Amount.HasValue || e.Currency == null) { result.Add(Display(e)); continue; }
            var group = (e.DisplayName.ToUpperInvariant(), e.Currency.ToUpperInvariant());
            if (overflow.Contains(group)) result.Add(Display(e));
            else if (emitted.Add(group)) result.Add(Display(e with { Amount = totals[group] }));
        }
        return result.ToArray();
    }

    public Task<CreditRun> StartAsync(bool preview, string expected) => Dispatch(() =>
    {
        CheckSession(expected);
        if (!preview && Problem != null) throw new InvalidOperationException(Problem);
        var config = Copy(settings);
        CreditSection[] sections;
        if (preview)
        {
            string[][] names = [["Северный маяк", "Лунный экспресс"], ["Orbit", "Pixel Harbor"],
                config.AggregateDonations
                    ? ["Лис в космосе — 250,00 RUB", "Comet — 20,00 USD", "Comet — 10,00 EUR", "Чайный капитан — 500,00 RUB"]
                    : ["Лис в космосе — 250,00 RUB", "Comet — 5,00 USD", "Comet — 10,00 EUR", "Comet — 15,00 USD", "Чайный капитан — 500,00 RUB"],
                ["Aurora", "Зелёный кит", "Nova"], ["Starlight", "Тихий берег"], ["Pixel Keeper", "Звёздный страж"]];
            sections = SectionKeys.Select((k, i) => new CreditSection(k, config.Headings[k], k == "moderators"
                ? names[i].Where(n => !config.ModeratorExclusions.Contains(ModeratorName(n), StringComparer.OrdinalIgnoreCase)).ToArray() : names[i]))
                .Where(s => config.EnabledSections[s.Key] && s.Names.Length > 0).ToArray();
        }
        else sections = SectionKeys.Where(k => config.EnabledSections[k]).Select(k => new CreditSection(k, config.Headings[k], Names(k, events.Where(e => Section(e) == k), config)))
            .Where(x => x.Names.Length > 0).ToArray();
        run = new(Guid.NewGuid().ToString("N"), sessionId, preview ? "preview" : "real", DateTimeOffset.UtcNow,
            config, sections, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        playback = sections.Length == 0 ? "finished" : "running";
        return run;
    });

    public Task<CreditRun?> RunAsync(string id) => Dispatch(() => run?.RunId == id ? run : null);
    public Task<bool> CompleteAsync(string id, string token) => Dispatch(() =>
    {
        if (run?.RunId != id || run.CompletionToken != token || playback != "running" || run.Settings.Loop) return false;
        playback = "finished"; return true;
    });

    public ValueTask DisposeAsync() => CloseAsync(null);

    // One reserved terminal action, after every admitted command and event.
    // No waiting writers can grow outside the bounded queue during Closing.
    internal ValueTask CloseAsync(string? expectedSession)
    {
        lock (gate)
        {
            if (closing != null) return new(closing);
            disposed = true;
            if (expectedSession != null)
                queue.Writer.TryWrite(() => { if (sessionId == expectedSession) NewSession(); });
            queue.Writer.TryComplete();
            closing = ReleaseAsync();
            return new(closing);
        }
    }
    private async Task ReleaseAsync()
    {
        try { await worker; }
        finally { lease.Dispose(); }
    }
}
