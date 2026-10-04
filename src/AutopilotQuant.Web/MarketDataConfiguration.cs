using System.Text.Json;
using System.Text.Json.Serialization;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.T4;

namespace AutopilotQuant.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketBinding(string Symbol, bool Enabled = true, string ExchangeId = "", string ProductId = "", string MarketId = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketDataSettings(string Provider, MarketBinding[] Instruments)
{
    public static MarketDataSettings Empty => new("none", [new("MES"), new("MNQ")]);
}
public sealed record MarketDataStatus(MarketDataSettings Settings, bool Configured, string Name, string Status,
    string Detail, string[] Missing, bool AdapterKeyPresent, bool T4ApiKeyPresent, bool Testing,
    T4ProbeResult? LastTest, string SimulatorEndpoint, bool ContinuousT4StreamingSupported = false, bool LiveRoutingEnabled = false);

public sealed class MarketDataConfiguration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly PaperSession _session;
    private readonly T4ConfigurationProbe _probe;
    private readonly string? _t4ApiKey;
    private readonly bool _adapterKeyPresent;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MarketDataSettings _settings;
    private T4ProbeResult? _lastTest;
    private volatile bool _testing;

    public MarketDataConfiguration(string directory, PaperSession session, T4ConfigurationProbe probe, string? adapterKey, string? t4ApiKey)
    {
        _path = Path.Combine(directory, "market-data.json");
        _session = session; _probe = probe; _t4ApiKey = t4ApiKey;
        _adapterKeyPresent = !string.IsNullOrWhiteSpace(adapterKey);
        if (File.Exists(_path) && new FileInfo(_path).Length > 64 * 1024)
            throw new InvalidDataException("Market-data settings exceed the size limit.");
        _settings = File.Exists(_path) ? Validate(JsonSerializer.Deserialize<MarketDataSettings>(File.ReadAllBytes(_path), Json)
            ?? throw new InvalidDataException("Market-data settings are empty.")) : MarketDataSettings.Empty;
    }

    public MarketDataStatus View()
    {
        var settings = Volatile.Read(ref _settings);
        var missing = Missing(settings);
        var name = settings.Provider switch { "external" => "External data adapter", "t4-simulator" => "T4 simulator", _ => "No provider selected" };
        var status = settings.Provider == "none" ? "Market data not configured" : missing.Length > 0 ? "Setup incomplete"
            : settings.Provider == "external" ? "External ingress ready — check instrument freshness"
            : _testing ? "Testing simulator configuration…" : "T4 settings saved — continuous streaming unavailable";
        var detail = settings.Provider == "external"
            ? "Only enabled, exact contract IDs are accepted. Provider connectivity and licensed data must be supplied by your external adapter."
            : settings.Provider == "t4-simulator"
                ? "Test login, contracts and current quotes with a short diagnostic connection. Continuous T4 streaming and historical backfill are not implemented."
                : "Choose your market-data source and select the expiring contracts. Prices are never generated.";
        return new(settings with { Instruments = settings.Instruments.ToArray() }, settings.Provider != "none" && missing.Length == 0,
            name, status, detail, missing, _adapterKeyPresent, !string.IsNullOrWhiteSpace(_t4ApiKey), _testing,
            Volatile.Read(ref _lastTest), T4ConfigurationProbe.SimulatorEndpoint);
    }

    public async Task<MarketDataStatus> SaveAsync(MarketDataSettings input)
    {
        var settings = Validate(input);
        await _gate.WaitAsync();
        try
        {
            // Feed admission uses the same gate. The core checks pause/exposure and clears old data atomically.
            await _session.ResetMarketDataAsync();
            var temporary = _path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(settings, Json));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _settings, settings);
            Volatile.Write(ref _lastTest, null);
            return View();
        }
        finally { _gate.Release(); }
    }

    public async Task<MarketDataStatus> TestAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("A market-data operation is already running.");
        try
        {
            var settings = _settings;
            if (settings.Provider != "t4-simulator") throw new InvalidOperationException("Select and save T4 simulator before testing.");
            if (Missing(settings).Length > 0) throw new InvalidOperationException("Complete the saved contract settings and server-side T4 API key before testing.");
            var session = await _session.ViewAsync();
            if (!session.Paused || session.Positions.Count != 0 || session.Pending.Count != 0)
                throw new InvalidOperationException("Pause and flatten before testing a provider connection.");
            _testing = true;
            Volatile.Write(ref _lastTest, null);
            var result = await _probe.CheckAsync(_t4ApiKey!, settings.Instruments.Where(m => m.Enabled)
                .Select(m => new T4Market(m.Symbol, m.ExchangeId, m.ProductId, m.MarketId)).ToArray(), session.Settings.QuoteMaxAgeSeconds, cancellationToken);
            Volatile.Write(ref _lastTest, result);
        }
        finally { _testing = false; _gate.Release(); }
        return View();
    }

    public async Task<SessionView> AcceptQuoteAsync(FeedQuote quote)
    {
        await _gate.WaitAsync();
        try { Authorize(quote.Symbol, quote.ContractId); return await _session.AcceptQuoteAsync(quote); }
        finally { _gate.Release(); }
    }
    public async Task<SessionView> AcceptBarAsync(FeedBar bar)
    {
        await _gate.WaitAsync();
        try { Authorize(bar.Bar?.Symbol, bar.ContractId); return await _session.AcceptBarAsync(bar); }
        finally { _gate.Release(); }
    }
    private void Authorize(string? symbol, string? marketId)
    {
        if (_settings.Provider != "external" || !_adapterKeyPresent)
            throw new InvalidOperationException("External market-data ingress is disabled for the selected provider.");
        if (Missing(_settings).Length != 0) throw new InvalidOperationException("Complete the external adapter contract settings first.");
        if (!_settings.Instruments.Any(m => m.Enabled && m.Symbol == symbol && m.MarketId == marketId))
            throw new ArgumentException("Feed contract does not match an enabled market-data binding.");
    }
    private string[] Missing(MarketDataSettings settings)
    {
        var missing = new List<string>();
        if (settings.Provider == "none") return ["Select a provider to prepare market data."];
        if (settings.Provider == "external" && !_adapterKeyPresent) missing.Add("Set MarketData__AdapterKey on the server and restart.");
        if (settings.Provider == "t4-simulator" && string.IsNullOrWhiteSpace(_t4ApiKey)) missing.Add("Set MarketData__T4__ApiKey on the server and restart.");
        if (!settings.Instruments.Any(m => m.Enabled)) missing.Add("Enable at least one instrument.");
        foreach (var market in settings.Instruments.Where(m => m.Enabled))
        {
            if (market.MarketId.Length == 0) missing.Add($"{market.Symbol}: choose the exact expiring market ID.");
            if (settings.Provider == "t4-simulator" && (market.ExchangeId.Length == 0 || market.ProductId.Length == 0))
                missing.Add($"{market.Symbol}: add the T4 exchange and product IDs.");
        }
        return missing.ToArray();
    }
    public static MarketDataSettings Validate(MarketDataSettings input)
    {
        if (input is null || input.Provider is not ("none" or "external" or "t4-simulator"))
            throw new ArgumentException("Provider must be none, external or t4-simulator.");
        if (input.Instruments is not { Length: 2 } || input.Instruments.Any(m => m is null)
            || !input.Instruments.Select(m => m.Symbol).Order().SequenceEqual(new[] { "MES", "MNQ" }))
            throw new ArgumentException("Provide exactly one MES and one MNQ binding.");
        string Identifier(string? value)
        {
            if (value is null || value.Length > 100 || value.Any(char.IsControl))
                throw new ArgumentException("Provider IDs must be text of at most 100 characters without control characters.");
            return value.Trim();
        }
        var bindings = input.Instruments.OrderBy(m => m.Symbol).Select(m => m with {
            ExchangeId = Identifier(m.ExchangeId), ProductId = Identifier(m.ProductId), MarketId = Identifier(m.MarketId)
        }).ToArray();
        var ids = bindings.Where(m => m.Enabled && m.MarketId.Length > 0).Select(m => m.MarketId).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("MES and MNQ must have distinct expiring market IDs.");
        return input with { Instruments = bindings };
    }
}
