using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RutonyChat
{
    public class Script
    {
        private object runtime;
        private MethodInfo record;
        private MethodInfo recordDonation;
        private MethodInfo configureRutonyRaids;
        private MethodInfo recordModerator;
        private MethodInfo setChannelOwner;
        private MethodInfo setChannelContext;
        private MethodInfo setChannelOwnerForContext;
        private Timer ownerTimer;
        private readonly Dictionary<string, string> publishedContexts = new Dictionary<string, string>();
        private CancellationTokenSource ownerCancellation;
        private readonly object ownerGate = new object();
        private string twitchOwner;
        private global::OAuth.VKPlayAPI vkOwnerApi;
        private string vkOwnerChannel;
        private long vkOwnerGeneration;
        private bool vkOwnerAttempted;

        public void InitParams(Dictionary<string, string> param)
        {
            string packageDirectory = ResolvePackageDirectory(param);
            string dataDirectory;
            if (!param.TryGetValue("DataDirectory", out dataDirectory) || string.IsNullOrWhiteSpace(dataDirectory))
                dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RutonyCredits");
            string portText;
            int port = 18765;
            if (param.TryGetValue("Port", out portText) && !int.TryParse(portText, out port))
                throw new ArgumentException("Rutony Credits: порт должен быть числом.");
            try
            {
                var library = Path.Combine(packageDirectory, "RutonyCredits.dll");
                // Do not reuse a loaded/manual assembly when a registered item is incomplete.
                if (!File.Exists(library)) throw new FileNotFoundException("Missing package library.", library);
                var assembly = Assembly.LoadFrom(library);
                var type = assembly.GetType("RutonyCredits.RuntimeHost", true);
                runtime = type.GetMethod("Start").Invoke(null, new object[] { packageDirectory, dataDirectory, port });
                record = type.GetMethod("Record");
                recordDonation = type.GetMethod("RecordDonation");
                configureRutonyRaids = type.GetMethod("ConfigureRutonyRaids");
                if (configureRutonyRaids == null) throw new MissingMethodException("Rutony Credits: обновите весь пакет и перезапустите RutonyChat.");
                recordModerator = type.GetMethod("RecordModerator");
                setChannelOwner = type.GetMethod("SetChannelOwner");
                setChannelContext = type.GetMethod("SetChannelContext");
                setChannelOwnerForContext = type.GetMethod("SetChannelOwnerForContext");
                ownerCancellation = new CancellationTokenSource();
                ChatEventServer.OnFollower += Follower;
                ChatEventServer.OnAnySubscriber += Subscriber;
                ChatEventServer.OnRaid += Raid;
                ChatEventServer.OnDonate += Donate;
                ChatEventServer.OnChatMessage += ChatMessage;
                var instance = runtime;
                var setNotifications = type.GetMethod("SetNotificationHandler");
                if (setNotifications != null)
                    setNotifications.Invoke(instance, new object[] { new Action<string>(message =>
                    {
                        // SayToWindow broadcasts through Rutony's WebSocket server.
                        // A captured WinForms context may have no message pump.
                        if (!ReferenceEquals(Volatile.Read(ref runtime), instance)) return;
                        try { RutonyBot.SayToWindow(FormatLinks(message, port)); } catch { }
                    }) });
                RefreshRutonyRaids();
                RefreshChannelOwner(ProgramProps.SiteEnum.twitch);
                RefreshChannelOwner(ProgramProps.SiteEnum.vkplay);
                // Observe connector changes even when the new channel has no chat yet.
                // Unchanged contexts do not enqueue commands or repeat API requests.
                ownerTimer = new Timer(_ =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref runtime), instance)) return;
                    RefreshRutonyRaids();
                    RefreshChannelOwner(ProgramProps.SiteEnum.twitch);
                    RefreshChannelOwner(ProgramProps.SiteEnum.vkplay);
                }, null, 1000, 1000);
                RutonyBot.SayToWindow(FormatLinks("Rutony Credits включён. Управление: http://127.0.0.1:" + port + "/control | OBS: http://127.0.0.1:" + port + "/overlay", port));
            }
            catch
            {
                Closing();
                RutonyBot.SayToWindow("Rutony Credits: не удалось запустить. Проверьте папку пакета, доступ к данным и свободный порт. После обновления DLL перезапустите RutonyChat.");
                throw;
            }
        }

        private void RefreshRutonyRaids()
        {
            var current = Volatile.Read(ref runtime);
            var configure = configureRutonyRaids;
            if (current == null || configure == null) return;
            int port = 0; bool secure = false;
            try
            {
                // Public server metadata only: no private TwitchClient or credentials.
                var host = typeof(ChatEventServer).Assembly.GetType("RutonyChat.ChatLocalServer", false);
                var field = host == null ? null : host.GetField("_HttpServer", BindingFlags.Public | BindingFlags.Static);
                var server = field == null ? null : field.GetValue(null);
                if (server != null)
                {
                    var type = server.GetType();
                    var listening = type.GetProperty("IsListening", BindingFlags.Public | BindingFlags.Instance);
                    var isListening = listening == null ? null : listening.GetValue(server, null);
                    if (isListening is bool && (bool)isListening)
                    {
                        var property = type.GetProperty("Port", BindingFlags.Public | BindingFlags.Instance);
                        var value = property == null ? null : property.GetValue(server, null);
                        if (value is int && (int)value > 0 && (int)value <= 65535) port = (int)value;
                        var tls = type.GetProperty("IsSecure", BindingFlags.Public | BindingFlags.Instance);
                        var isSecure = tls == null ? null : tls.GetValue(server, null);
                        secure = isSecure is bool && (bool)isSecure;
                    }
                }
            }
            catch { port = 0; secure = false; }
            if (!ReferenceEquals(Volatile.Read(ref runtime), current)) return;
            try { configure.Invoke(current, new object[] { port, secure }); }
            catch { /* Closing or server replacement: the next timer tick retries. */ }
        }
        private static string ResolvePackageDirectory(Dictionary<string, string> parameters)
        {
            string explicitDirectory;
            if (parameters.TryGetValue("PackageDirectory", out explicitDirectory) && !string.IsNullOrWhiteSpace(explicitDirectory))
                return explicitDirectory;
            string workshopDirectory = null;
            try { workshopDirectory = RutonyBotFunctions.GetScriptDirectory("rutony_credits.cs"); }
            catch { /* SDK lookup unavailable: preserve the manual installation fallback. */ }
            if (!string.IsNullOrWhiteSpace(workshopDirectory))
                return Path.Combine(workshopDirectory, "RutonyCredits");
            return Path.Combine(AppContext.BaseDirectory, "scripts", "RutonyCredits");
        }

        public void Closing()
        {
            CancellationTokenSource cancellation;
            Timer timer;
            object old;
            lock (ownerGate)
            {
                old = runtime; runtime = null;
                cancellation = ownerCancellation; ownerCancellation = null;
                timer = ownerTimer; ownerTimer = null;
                twitchOwner = null; vkOwnerApi = null; vkOwnerChannel = null;
                publishedContexts.Clear();
                vkOwnerGeneration++; vkOwnerAttempted = false;
            }
            if (timer != null) timer.Dispose();
            if (cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); }
            ChatEventServer.OnFollower -= Follower;
            ChatEventServer.OnAnySubscriber -= Subscriber;
            ChatEventServer.OnRaid -= Raid;
            ChatEventServer.OnDonate -= Donate;
            ChatEventServer.OnChatMessage -= ChatMessage;
            if (old != null) ((IDisposable)old).Dispose();
        }

        private static string FormatLinks(string message, int port)
        {
            var html = WebUtility.HtmlEncode(message);
            var control = "http://127.0.0.1:" + port + "/control";
            var overlay = "http://127.0.0.1:" + port + "/overlay";
            html = html.Replace(WebUtility.HtmlEncode(control), "<a href=\"" + control + "\" target=\"_blank\" rel=\"noopener noreferrer\">Открыть панель титров</a>");
            return html.Replace(WebUtility.HtmlEncode(overlay), "<a href=\"" + overlay + "\" target=\"_blank\" rel=\"noopener noreferrer\">Экран титров для OBS</a>");
        }

        private static bool IsCurrentVkConnection(global::OAuth.VKPlayAPI api, string channel)
        {
            try
            {
                var service = ChatServer.GetParent(ProgramProps.SiteEnum.vkplay) ?? ChatServer.GetChatServer(ProgramProps.SiteEnum.vkplay);
                if (service == null || service.State != global::RutonyChat.ChatServices.ChatService.StateEnum.OK
                    || api.State != global::OAuth.OAuthBase.StatesAuth.ready) return false;
                var field = service.GetType().GetField("ServiceAPI", BindingFlags.Public | BindingFlags.Instance);
                return field != null && ReferenceEquals(field.GetValue(service), api)
                    && string.Equals(service.GetChannelPath() ?? "", channel, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string ChannelContext(ProgramProps.SiteEnum site, string path)
        {
            Uri uri;
            if (!Uri.TryCreate(path, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https")
                || uri.AbsolutePath.Trim('/').Length == 0) return null;
            string context;
            if (site == ProgramProps.SiteEnum.twitch)
            {
                if (uri.Host != "twitch.tv" && uri.Host != "www.twitch.tv") return null;
                context = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/').Split('/')[0]).ToLowerInvariant();
            }
            else context = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            // Publish unusable connector identifiers as unknown. A store validation
            // rejection happens before admission and cannot invalidate a previous owner.
            if (string.IsNullOrWhiteSpace(context) || context.Length > 2048) return null;
            foreach (var character in context) if (char.IsControl(character)) return null;
            return context;
        }

        private bool PublishContext(object current, ProgramProps.SiteEnum site, string context)
        {
            var key = site.ToString();
            string previous;
            if (publishedContexts.TryGetValue(key, out previous) && previous == context) return true;
            if (setChannelContext != null && !(bool)setChannelContext.Invoke(current, new object[] { key, context }))
            {
                publishedContexts.Remove(key); // the store queued a safety invalidation
                return false;
            }
            publishedContexts[key] = context;
            return true;
        }

        private bool PublishOwner(object current, ProgramProps.SiteEnum site, string context, string name, string id)
        {
            if (setChannelOwnerForContext != null)
                return (bool)setChannelOwnerForContext.Invoke(current, new object[] { site.ToString(), context, name, id });
            // Older DLLs still expose the original reflection boundary.
            setChannelOwner.Invoke(current, new object[] { site.ToString(), name, id });
            return true;
        }

        private void RefreshChannelOwner(ProgramProps.SiteEnum site)
        {
            if (site != ProgramProps.SiteEnum.twitch && site != ProgramProps.SiteEnum.vkplay) return;
            try
            {
                // Read the connector inside the publication lock: an older callback
                // must not overwrite context observed by a newer callback.
                lock (ownerGate)
                {
                    var current = runtime;
                    var cancellation = ownerCancellation;
                    if (current == null || setChannelOwner == null || cancellation == null) return;
                    var service = ChatServer.GetParent(site) ?? ChatServer.GetChatServer(site);
                    var path = service == null ? null : service.GetChannelPath();
                    var context = ChannelContext(site, path);
                    if (!PublishContext(current, site, context)) return; // retry on next observation after overload
                    if (site == ProgramProps.SiteEnum.twitch)
                    {
                        if (context == null) { twitchOwner = null; return; }
                        if (service.State != global::RutonyChat.ChatServices.ChatService.StateEnum.OK) return;
                        if (context != twitchOwner && PublishOwner(current, site, context, context, null)) twitchOwner = context;
                        return;
                    }
                    // Public API only: never inspect credentials or initiate login.
                    var field = service == null ? null : service.GetType().GetField("ServiceAPI", BindingFlags.Public | BindingFlags.Instance);
                    var api = field == null ? null : field.GetValue(service) as global::OAuth.VKPlayAPI;
                    if (!ReferenceEquals(vkOwnerApi, api) || vkOwnerChannel != path)
                    {
                        vkOwnerApi = api; vkOwnerChannel = path;
                        vkOwnerGeneration++; vkOwnerAttempted = false;
                    }
                    if (context == null || service.State != global::RutonyChat.ChatServices.ChatService.StateEnum.OK
                        || api == null || api.State != global::OAuth.OAuthBase.StatesAuth.ready || vkOwnerAttempted) return;
                    vkOwnerAttempted = true;
                    var generation = vkOwnerGeneration;
                    var token = cancellation.Token;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                                if (!IsCurrentVkConnection(api, path)) return;
                                var info = await api.GetChannelInfoAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                                if (info == null || info.data == null || info.data.owner == null || info.data.owner.id <= 0) return;
                                lock (ownerGate)
                                {
                                    // Recheck after taking the lock as well as after awaiting:
                                    // both the native connection and this runtime generation matter.
                                    if (generation != vkOwnerGeneration || token.IsCancellationRequested
                                        || !ReferenceEquals(Volatile.Read(ref runtime), current)
                                        || !IsCurrentVkConnection(api, path)) return;
                                    if (!PublishOwner(current, site, context, info.data.owner.nick,
                                        info.data.owner.id.ToString(CultureInfo.InvariantCulture)))
                                        vkOwnerAttempted = false;
                                }
                            }
                        }
                        catch { /* Optional lookup: no new authorization or background retries on failure. */ }
                    });
                }
            }
            catch { /* Unavailable public API: manual exclusions remain available. */ }
        }

        private void Save(ProgramProps.SiteEnum site, string kind, string name)
        {
            RefreshChannelOwner(site);
            var current = runtime;
            if (current != null) record.Invoke(current, new object[] { site.ToString(), kind, name });
        }
        private void ChatMessage(ProgramProps.SiteEnum site, string name, string text, Dictionary<string, object> parameters)
        {
            RefreshChannelOwner(site);
            var current = runtime;
            object role;
            if (current == null || recordModerator == null || parameters == null
                || !parameters.TryGetValue("moderator", out role) || !(role is bool) || !(bool)role) return;
            object user;
            string userId = parameters.TryGetValue("user_id", out user) ? user as string : null;
            if (string.IsNullOrWhiteSpace(userId) && parameters.TryGetValue("userId", out user)) userId = user as string;
            recordModerator.Invoke(current, new object[] { site.ToString(), name, userId });
        }
        private void Follower(ProgramProps.SiteEnum site, string name) { Save(site, "follower", name); }
        private void Subscriber(ProgramProps.SiteEnum site, string name) { Save(site, "subscriber", name); }
        private void Raid(ProgramProps.SiteEnum site, string name, int quantity) { Save(site, "raid", name); }
        private void Donate(ProgramProps.SiteEnum site, string name, CurrencyControl.SumClass sum, string text, string textInChat)
        {
            RefreshChannelOwner(site);
            var current = runtime;
            if (current == null) return;
            if (recordDonation == null) { Save(site, "donation", name); return; }
            decimal value; decimal? amount = null;
            if (sum != null && decimal.TryParse(sum.sum.ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0) amount = value;
            string currency = sum != null && sum.currency != null ? sum.currency.CharCode : null;
            recordDonation.Invoke(current, new object[] { site.ToString(), name, amount, currency });
        }
    }
}
