using System.Text.Json;
using System.Text.Json.Serialization;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.T4;

namespace AutopilotQuant.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketBinding(string Symbol, bool Enabled = true, string ExchangeId = "", string ProductId = "", string MarketId = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketDataSettings(string Provider, MarketBinding[] Instruments, string HistoryTimeZone = "unconfirmed")
{
    public static MarketDataSettings Empty => new("none", [new("MES"), new("MNQ")]);
}
public sealed record T4AuthenticationStatus(string Method, bool Configured, string[] MissingFields);
public sealed record MarketDataStatus(MarketDataSettings Settings, bool Configured, string Name, string Status,
    string Detail, string[] Missing, bool AdapterKeyPresent, bool T4ApiKeyPresent, bool Testing,
    T4ProbeResult? LastTest, string SimulatorEndpoint, bool ContinuousT4StreamingSupported = true, bool LiveRoutingEnabled = false,
    T4StreamView? Connection = null, T4AuthenticationStatus? Authentication = null);

public sealed class MarketDataConfiguration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly PaperSession _session;
    private readonly T4ConfigurationProbe _probe;
    private readonly T4Credentials _credentials;
    private readonly bool _adapterKeyPresent;
    private readonly T4MarketDataService? _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MarketDataSettings _settings;
    private T4ProbeResult? _lastTest;
    private volatile bool _testing;

    public MarketDataConfiguration(string directory, PaperSession session, T4ConfigurationProbe probe, string? adapterKey, T4Credentials? credentials,
        T4MarketDataService? stream = null)
    {
        _path = Path.Combine(directory, "market-data.json");
        _session = session; _probe = probe; _credentials = credentials ?? new();
        _stream = stream;
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
        var status = settings.Provider == "none" || missing.Length > 0 ? "MES/MNQ futures feed not configured"
            : settings.Provider == "external" ? "External ingress ready — check instrument freshness"
            : _testing ? "Testing simulator configuration…" : _stream?.View().Message ?? "T4 settings saved — ready to connect";
        var detail = settings.Provider == "external"
            ? "Only enabled, exact contract IDs are accepted. Provider connectivity and licensed data must be supplied by your external adapter."
            : settings.Provider == "t4-simulator"
                ? missing.Length > 0
                    ? "T4 simulator needs server credentials and exact futures contract IDs. Use an API key or a simulator login with a CTS application license. Open Market data for missing items. Public.com ETF quotes are a separate reference connection."
                    : "Connect your authorized MES/MNQ feed for continuous quotes and candles. Choose the historical timestamp timezone to load completed bars before streaming."
                : "Choose your market-data source and select the expiring contracts. Prices are never generated.";
        return new(settings with { Instruments = settings.Instruments.ToArray() }, settings.Provider != "none" && missing.Length == 0,
            name, status, detail, missing, _adapterKeyPresent, _credentials.ApiKeyPresent, _testing,
            Volatile.Read(ref _lastTest), T4ConfigurationProbe.SimulatorEndpoint, Connection: _stream?.View(),
            Authentication: new(_credentials.Method, _credentials.Configured, _credentials.MissingFields));
    }

    public async Task<MarketDataStatus> SaveAsync(MarketDataSettings input)
    {
        var settings = Validate(input);
        await _gate.WaitAsync();
        try
        {
            if (_stream?.Active == true) throw new InvalidOperationException("Disconnect the T4 feed before changing provider settings.");
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
            if (_stream?.Active == true) throw new InvalidOperationException("Disconnect before running the separate diagnostic test.");
            if (settings.Provider != "t4-simulator") throw new InvalidOperationException("Select and save T4 simulator before testing.");
            if (Missing(settings).Length > 0) throw new InvalidOperationException("Complete the saved contract settings and server-side T4 credentials before testing.");
            var session = await _session.ViewAsync();
            if (!session.Paused || session.Positions.Count != 0 || session.Pending.Count != 0)
                throw new InvalidOperationException("Pause and flatten before testing a provider connection.");
            _testing = true;
            Volatile.Write(ref _lastTest, null);
            var result = await _probe.CheckAsync(_credentials, settings.Instruments.Where(m => m.Enabled)
                .Select(m => new T4Market(m.Symbol, m.ExchangeId, m.ProductId, m.MarketId)).ToArray(), session.Settings.QuoteMaxAgeSeconds, cancellationToken);
            Volatile.Write(ref _lastTest, result);
        }
        finally { _testing = false; _gate.Release(); }
        return View();
    }

    public async Task<MarketDataStatus> ConnectAsync()
    {
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException("A market-data operation is already running.");
        try
        {
            if (_settings.Provider != "t4-simulator" || Missing(_settings).Length > 0)
                throw new InvalidOperationException("Save complete T4 simulator IDs and configure server credentials before connecting.");
            if (_stream is null) throw new InvalidOperationException("T4 streaming service is unavailable.");
            var session = await _session.ViewAsync();
            await _stream.ConnectAsync(_credentials, _settings.Instruments.Where(m => m.Enabled)
                .Select(m => new T4Market(m.Symbol, m.ExchangeId, m.ProductId, m.MarketId)).ToArray(), _settings.HistoryTimeZone, session.Settings.QuoteMaxAgeSeconds);
            return View();
        }
        finally { _gate.Release(); }
    }
    public async Task<MarketDataStatus> DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_settings.Provider != "t4-simulator") throw new InvalidOperationException("T4 is not the selected provider.");
            if (_stream is not null) await _stream.DisconnectAsync();
            return View();
        }
        finally { _gate.Release(); }
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
        if (settings.Provider == "t4-simulator" && !_credentials.Configured)
        {
            if (_credentials.Method == "not-configured")
                missing.Add("Set MarketData__T4__ApiKey, or Firm, Username, Password, AppName and AppLicense under MarketData:T4 on the server, then restart.");
            else
                missing.AddRange(_credentials.MissingFields.Select(field => $"Set MarketData__T4__{field} on the server and restart (or use ApiKey authentication)."));
        }
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
        if (input.HistoryTimeZone is not ("unconfirmed" or "CST" or "America/Chicago"))
            throw new ArgumentException("Historical timezone must be unconfirmed, CST or America/Chicago.");
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
