using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.Replay;
using AutopilotQuant.Web;
using AutopilotQuant.T4;

var builder = WebApplication.CreateBuilder(args);
// Optional, Git-ignored local secrets; environment and CLI values take precedence.
builder.Configuration.AddJsonFile(Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,
    "..", "..", "config", "appsettings.Local.json")), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);
var port = builder.Configuration.GetValue("Dashboard:Port", 5080);
if (port is < 1 or > 65535) throw new InvalidOperationException("Dashboard port must be 1–65535.");
// This release is deliberately local-only; URLs/ASPNETCORE_URLS cannot expose it publicly.
builder.WebHost.ConfigureKestrel(server =>
{
    server.Listen(IPAddress.Loopback, port);
    server.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
});
var directory = Path.GetFullPath(builder.Configuration["Dashboard:DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "dashboard"));
Directory.CreateDirectory(directory);
var keyPath = Path.Combine(directory, "dashboard.key");
var dashboardKey = builder.Configuration["Dashboard:AccessKey"];
if (string.IsNullOrEmpty(dashboardKey))
{
    if (!File.Exists(keyPath))
    {
        using var keyFile = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        keyFile.Write(Encoding.UTF8.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
        keyFile.Flush(flushToDisk: true);
    }
    dashboardKey = File.ReadAllText(keyPath).Trim();
}
if (dashboardKey.Length is < 32 or > 256) throw new InvalidOperationException("Dashboard access key must have 32–256 characters.");
var adapterKey = builder.Configuration["MarketData:AdapterKey"];
if (!string.IsNullOrEmpty(adapterKey) && adapterKey.Length is < 32 or > 256)
    throw new InvalidOperationException("Adapter key must have 32–256 characters.");
if (!string.IsNullOrEmpty(adapterKey) && adapterKey == dashboardKey)
    throw new InvalidOperationException("Dashboard and adapter keys must be different.");
builder.Services.AddSingleton(_ => new PaperSessionStore(directory));
builder.Services.AddSingleton<PaperSession>();
builder.Services.AddSingleton(new T4ConfigurationProbe());
builder.Services.AddSingleton(services => new MarketDataConfiguration(directory,
    services.GetRequiredService<PaperSession>(), services.GetRequiredService<T4ConfigurationProbe>(),
    adapterKey, builder.Configuration["MarketData:T4:ApiKey"]));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new OffsetTimestampConverter()));
builder.Services.AddSingleton(new ReplayArchive(directory));
builder.Services.AddHostedService<SessionMonitor>();
var app = builder.Build();
// Resolve now: a second writer or corrupt state must fail startup, not a later request.
_ = app.Services.GetRequiredService<PaperSession>();
_ = app.Services.GetRequiredService<MarketDataConfiguration>();
app.Logger.LogInformation("Local paper dashboard: http://127.0.0.1:{Port}. Access key location: {KeyPath}", port,
    builder.Configuration["Dashboard:AccessKey"] is null ? keyPath : "Dashboard__AccessKey environment variable");

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
        || context.Request.Host.Host is not ("127.0.0.1" or "localhost")
        || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
    {
        context.Response.StatusCode = 403;
        return;
    }
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}")
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var feed = context.Request.Path.StartsWithSegments("/api/feed");
        var expected = feed ? adapterKey : dashboardKey;
        if (feed && string.IsNullOrEmpty(expected))
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "No market-data adapter is configured." });
            return;
        }
        var supplied = context.Request.Headers[feed ? "X-Adapter-Key" : "X-Dashboard-Key"].ToString();
        if (supplied.Length > 256 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected!)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unlock with your local dashboard access key." });
            return;
        }
    }
    try { await next(context); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or JsonException or FormatException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        app.Logger.LogError(ex, "Local storage operation failed");
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "Local storage failed. Check the server and data directory before restarting." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/state", async (PaperSession session, MarketDataConfiguration marketData) => new
{
    session = await session.ViewAsync(),
    provider = marketData.View()
});
app.MapGet("/api/market-data", (MarketDataConfiguration marketData) => marketData.View());
app.MapPut("/api/market-data", async (MarketDataSettings settings, MarketDataConfiguration marketData) => await marketData.SaveAsync(settings));
app.MapPost("/api/market-data/test", async (MarketDataConfiguration marketData, CancellationToken ct) => await marketData.TestAsync(ct));
app.MapPost("/api/control/{action}", async (string action, PaperSession session) => await session.ControlAsync(action));
app.MapPut("/api/settings", async (ForwardSettings settings, PaperSession session) => await session.ConfigureAsync(settings));
app.MapPost("/api/feed/quotes", async (FeedQuote quote, MarketDataConfiguration marketData) => await marketData.AcceptQuoteAsync(quote));
app.MapPost("/api/feed/bars", async (FeedBar bar, MarketDataConfiguration marketData) => await marketData.AcceptBarAsync(bar));
app.MapGet("/api/replays", (ReplayArchive archive) => archive.List());
app.MapGet("/api/replays/{id:guid}", (Guid id, ReplayArchive archive) =>
    archive.Read(id) is { } run ? Results.Json(run) : Results.NotFound());
app.MapPost("/api/replays", async (HttpRequest request, ReplayArchive archive, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var bars = await HistoricalBarCsvReader.ReadAsync(reader, ct);
    if (bars.Count > 5000) throw new ArgumentException("Dashboard replay supports at most 5,000 bars per run; use the CLI for larger files.");
    var name = request.Headers["X-File-Name"].ToString();
    return await archive.RunAsync(name, bars, ct);
});
app.Run();
