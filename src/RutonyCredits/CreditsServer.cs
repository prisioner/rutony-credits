using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RutonyCredits;

public sealed class CreditsServer : IAsyncDisposable
{
    private readonly WebApplication app;
    public string BaseUrl { get; }
    public string ManagementToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private CreditsServer(WebApplication app, int port) { this.app = app; BaseUrl = $"http://127.0.0.1:{port}"; }

    // The embedded web host must never register console signals or terminate RutonyChat.
    private sealed class EmbeddedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public static async Task<CreditsServer> StartAsync(CreditStore store, string webRoot, int port, string hostMode = "rutony", ObsController? obs = null, RutonyRaidSource? twitchRaids = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(CreditsServer).Assembly.GetName().Name,
            ContentRootPath = Path.GetFullPath(webRoot)
        });
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
        builder.WebHost.UseKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = 16384;
            o.Listen(IPAddress.Loopback, port);
        });
        var app = builder.Build();
        var server = new CreditsServer(app, port);
        server.Routes(store, Path.GetFullPath(webRoot), hostMode, obs, twitchRaids);
        try { await app.StartAsync(); return server; }
        catch { await app.DisposeAsync(); throw; }
    }

    private void Routes(CreditStore store, string webRoot, string hostMode, ObsController? obs, RutonyRaidSource? twitchRaids)
    {
        var host = new Uri(BaseUrl).Authority;
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
            if (request.Host.Value != host || (request.Headers.Origin.Count > 0 && request.Headers.Origin != BaseUrl)
                || request.Headers["Sec-Fetch-Site"] == "cross-site")
            { context.Response.StatusCode = 403; return; }
            if (request.Method == "POST")
            {
                if (!request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
                bool complete = request.Path.StartsWithSegments("/api/runs") && request.Path.Value!.EndsWith("/complete", StringComparison.Ordinal);
                if (!complete && request.Headers["X-Credits-Token"] != ManagementToken)
                { context.Response.StatusCode = 403; return; }
            }
            try { await next(context); }
            catch (StoreOverloadedException e)
            { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (InvalidOperationException e)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (ArgumentException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Проверьте параметры запроса." }); }
            catch (JsonException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Некорректный JSON." }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Ошибка локального хранилища. Проверьте доступ к диску." }); }
        });
        app.MapGet("/", () => Results.Redirect("/control"));
        app.MapGet("/control", () => Results.Content(BoundedFiles.Text(Path.Combine(webRoot, "control.html"), 1024 * 1024)
            .Replace("__TOKEN__", ManagementToken, StringComparison.Ordinal), "text/html; charset=utf-8"));
        app.MapGet("/overlay", () => Results.File(Path.Combine(webRoot, "overlay.html"), "text/html; charset=utf-8"));
        foreach (var file in new[] { "control.js", "overlay.js", "playback.js", "local-font.js", "font-picker.js", "control.css", "overlay.css" })
        {
            var name = file;
            app.MapGet("/" + name, () => Results.File(Path.Combine(webRoot, name), name.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8"));
        }
        app.MapGet("/api/obs", () => Results.Json(obs?.Status ?? new ObsStatus(new ObsOptions(), false, "Не подключено", null, false, []) { Available = false }, Wire.Json));
        app.MapPost("/api/obs", async (HttpRequest req) =>
        {
            if (obs == null) throw new InvalidOperationException("Подключение OBS доступно при работе скрипта в RutonyChat.");
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException();
            if (!root.TryGetProperty("options", out var value) || value.ValueKind != JsonValueKind.Object) throw new ArgumentException();
            string? password = null;
            if (root.TryGetProperty("password", out var secret) && secret.ValueKind != JsonValueKind.Null)
            {
                if (secret.ValueKind != JsonValueKind.String) throw new ArgumentException();
                password = secret.GetString();
                if (password?.Length > 1024) throw new ArgumentException();
            }
            bool clearPassword = false;
            if (root.TryGetProperty("clearPassword", out var clear))
            {
                if (clear.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException();
                clearPassword = clear.GetBoolean();
            }
            await obs.ConfigureAsync(value.Deserialize<ObsOptions>(Wire.Json) ?? throw new ArgumentException(), password, clearPassword);
            return Results.Ok();
        });
        app.MapGet("/api/fonts", () => Results.Json(SystemFonts.Read()));
        app.MapGet("/api/font-faces", (HttpRequest request) =>
        {
            var family = request.Query["family"];
            if (family.Count != 1 || !SystemFonts.ValidName(family[0])) throw new ArgumentException();
            return Results.Json(SystemFonts.ReadFaces(family[0]!), Wire.Json);
        });
        app.MapGet("/api/state", async () => Results.Json((await store.StateAsync()) with { HostMode = hostMode, TwitchRaids = twitchRaids?.Status ?? new(false, false, "Не используется", null) }, Wire.Json));
        app.MapGet("/api/runs/{id}", async (string id) => await store.RunAsync(id) is { } run ? Results.Json(run, Wire.Json) : Results.NotFound());
        app.MapPost("/api/run", async (HttpRequest req) => Results.Json(await store.StartAsync(false, await Session(req)), Wire.Json));
        app.MapPost("/api/preview", async (HttpRequest req) => Results.Json(await store.StartAsync(true, await Session(req)), Wire.Json));
        app.MapPost("/api/stop", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var session = String(root, "sessionId");
            if (!root.TryGetProperty("runId", out var run) || run.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException();
            await store.StopAsync(session, run.GetString()); return Results.Ok();
        });
        app.MapPost("/api/reset", async (HttpRequest req) =>
        {
            var session = await Session(req);
            if (obs != null) await obs.CancelPendingResetAsync();
            return Results.Json(new { sessionId = await store.ResetAsync(session) });
        });
        app.MapPost("/api/settings", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var session = String(root, "sessionId");
            if (!root.TryGetProperty("settings", out var value)) throw new ArgumentException();
            await store.SetSettingsAsync(value.Deserialize<CreditSettings>(Wire.Json) ?? throw new ArgumentException(), session);
            return Results.Ok();
        });
        app.MapPost("/api/runs/{id}/complete", async (string id, HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            return await store.CompleteAsync(id, String(doc.RootElement, "token")) ? Results.Ok() : Results.Conflict();
        });
    }

    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ArgumentException();
    private static async Task<string> Session(HttpRequest req)
    { using var doc = await JsonDocument.ParseAsync(req.Body); return String(doc.RootElement, "sessionId"); }

    public async ValueTask DisposeAsync()
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(timeout.Token); await app.DisposeAsync(); }
}
