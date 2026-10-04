using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AutopilotQuant.Web;

public sealed record PublicReferenceQuote(string Symbol, string Status, decimal? Last = null,
    DateTimeOffset? LastAt = null, decimal? Bid = null, DateTimeOffset? BidAt = null,
    decimal? Ask = null, DateTimeOffset? AskAt = null, bool LastFresh = false,
    bool BidFresh = false, bool AskFresh = false);
public sealed record PublicMarketDataView(bool SecretPresent, string Status, string Message,
    bool Refreshing = false, string? AccountLabel = null, DateTimeOffset? RequestedAt = null,
    DateTimeOffset? LastSuccessAt = null, DateTimeOffset? NextRequestAt = null,
    PublicReferenceQuote[]? Quotes = null, bool FuturesSupported = false, bool LiveRoutingEnabled = false);

// Read-only reference quotes. This client has no PaperSession dependency or order methods.
public sealed class PublicMarketDataClient
{
    private static readonly string[] Symbols = ["SPY", "QQQ"];
    private const string Origin = "https://api.public.com";
    private readonly HttpClient _http;
    private readonly string? _secret;
    private readonly string? _configuredAccount;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PublicMarketDataView _view;
    private string? _token, _account;
    private DateTimeOffset _tokenExpires;

    public PublicMarketDataClient(HttpClient http, string? secret, string? accountId = null, TimeProvider? clock = null)
    {
        _http = http; _secret = secret; _configuredAccount = accountId; _clock = clock ?? TimeProvider.System;
        var configured = !string.IsNullOrWhiteSpace(secret);
        _view = new(configured, configured ? "ready" : "not-configured", configured
            ? "Public.com key present. Refresh to authenticate and request SPY/QQQ ETF quotes."
            : "Set MarketData:Public:Secret on the server to enable Public.com ETF reference quotes.", Quotes: []);
    }

    public PublicMarketDataView View()
    {
        var view = Volatile.Read(ref _view);
        var now = _clock.GetUtcNow();
        bool Fresh(DateTimeOffset? at) => at is { } timestamp && now - timestamp <= TimeSpan.FromSeconds(30)
            && timestamp <= now.AddSeconds(2);
        return view with { Quotes = (view.Quotes ?? []).Select(q => q with {
            LastFresh = q.Last.HasValue && Fresh(q.LastAt), BidFresh = q.Bid.HasValue && Fresh(q.BidAt),
            AskFresh = q.Ask.HasValue && Fresh(q.AskAt)
        }).ToArray() };
    }

    public async Task<PublicMarketDataView> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_view.SecretPresent || !await _gate.WaitAsync(0, cancellationToken)) return View();
        try
        {
            var now = _clock.GetUtcNow();
            if (_view.NextRequestAt is { } next && now < next) return View();
            Publish(_view with { Status = "refreshing", Message = "Requesting Public.com reference quotes…",
                Refreshing = true, RequestedAt = now, NextRequestAt = now.AddSeconds(15), Quotes = [] });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var ct = timeout.Token;
            if (_token is null || now >= _tokenExpires)
            {
                using var auth = await SendAsync(HttpMethod.Post, "/userapiauthservice/personal/access-tokens",
                    new { secret = _secret, validityInMinutes = 15 }, false, ct);
                _token = auth.RootElement.GetProperty("accessToken").GetString();
                if (string.IsNullOrWhiteSpace(_token) || _token.Length > 16384 || _token.Any(char.IsControl))
                    throw new InvalidDataException();
                _tokenExpires = now.AddMinutes(14);
            }
            if (_account is null)
            {
                using var accounts = await SendAsync(HttpMethod.Get, "/userapigateway/trading/account", null, true, ct);
                _account = ChooseAccount(accounts.RootElement);
                Publish(_view with { AccountLabel = "Public account · …" + _account[^Math.Min(4, _account.Length)..] });
            }
            using var response = await SendAsync(HttpMethod.Post,
                $"/userapigateway/marketdata/{Uri.EscapeDataString(_account)}/quotes",
                new { instruments = Symbols.Select(symbol => new { symbol, type = "EQUITY" }).ToArray() }, true, ct);
            var quotes = ParseQuotes(response.RootElement);
            var success = quotes.Any(q => q.Status == "available");
            Publish(_view with { Status = success ? "snapshot" : "no-quotes", Refreshing = false, Quotes = quotes,
                LastSuccessAt = _clock.GetUtcNow(), Message = success
                    ? "Public.com ETF snapshots received. Check source timestamps; closed-market quotes may be stale."
                    : "Public.com returned no usable SPY/QQQ quotes. Check market-data permissions and availability." });
        }
        catch (ProviderFailure failure)
        {
            if (failure.Status == "authentication-failed") { _token = null; _account = null; }
            Publish(_view with { Status = failure.Status, Message = failure.Message, Refreshing = false, Quotes = [],
                NextRequestAt = _clock.GetUtcNow().Add(failure.RetryAfter ?? TimeSpan.FromSeconds(15)) });
        }
        catch (OperationCanceledException)
        {
            Publish(_view with { Status = "timeout", Message = "Public.com request timed out or was cancelled. Refresh to retry.", Refreshing = false, Quotes = [] });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            // Provider payloads, account IDs, secrets, tokens and exception messages never reach the browser or logs.
            Publish(_view with { Status = "unavailable", Message = "Public.com could not return a valid quote snapshot. Check the connection and retry.", Refreshing = false, Quotes = [] });
        }
        finally { _gate.Release(); }
        return View();
    }

    private void Publish(PublicMarketDataView view) => Volatile.Write(ref _view, view);

    private string ChooseAccount(JsonElement root)
    {
        var array = root.GetProperty("accounts");
        if (array.GetArrayLength() > 100) throw new InvalidDataException();
        var accounts = array.EnumerateArray().Select(a => new {
            Id = a.GetProperty("accountId").GetString() ?? "", Type = a.GetProperty("accountType").GetString()
        }).ToArray();
        if (accounts.Any(a => a.Id.Length is < 1 or > 256 || a.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new InvalidDataException();
        if (!string.IsNullOrWhiteSpace(_configuredAccount))
        {
            if (accounts.Any(a => a.Id == _configuredAccount)) return _configuredAccount;
            throw new ProviderFailure("account-required", "The configured Public.com account is unavailable to this key.");
        }
        var brokerage = accounts.Where(a => a.Type == "BROKERAGE").ToArray();
        if (brokerage.Length == 1) return brokerage[0].Id;
        if (accounts.Length == 1) return accounts[0].Id;
        throw new ProviderFailure("account-required", "Set MarketData:Public:AccountId on the server to select one Public.com account, then restart.");
    }

    private static PublicReferenceQuote[] ParseQuotes(JsonElement root)
    {
        var array = root.GetProperty("quotes");
        if (array.GetArrayLength() > Symbols.Length) throw new InvalidDataException();
        var result = new Dictionary<string, PublicReferenceQuote>(StringComparer.Ordinal);
        foreach (var quote in array.EnumerateArray())
        {
            var instrument = quote.GetProperty("instrument");
            var symbol = instrument.GetProperty("symbol").GetString() ?? "";
            if (!Symbols.Contains(symbol) || instrument.GetProperty("type").GetString() != "EQUITY" || result.ContainsKey(symbol))
                throw new InvalidDataException();
            if (quote.GetProperty("outcome").GetString() != "SUCCESS") { result.Add(symbol, new(symbol, "unavailable")); continue; }
            var last = Price(quote, "last"); var bid = Price(quote, "bid"); var ask = Price(quote, "ask");
            if (bid.HasValue && ask.HasValue && bid > ask) throw new InvalidDataException();
            result.Add(symbol, new(symbol, last.HasValue || bid.HasValue || ask.HasValue ? "available" : "unavailable",
                last, Timestamp(quote, "lastTimestamp"), bid, Timestamp(quote, "bidTimestamp"), ask, Timestamp(quote, "askTimestamp")));
        }
        return Symbols.Select(s => result.GetValueOrDefault(s) ?? new(s, "unavailable")).ToArray();
    }

    private static decimal? Price(JsonElement quote, string name)
    {
        if (!quote.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) || price < 0 || price > 10_000_000)
            throw new InvalidDataException();
        return price == 0 ? null : price;
    }

    private static DateTimeOffset? Timestamp(JsonElement quote, string name)
    {
        if (!quote.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.GetString();
        if (text is null || !text.Contains('T') || !(text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-')
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            throw new InvalidDataException();
        return timestamp;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Origin + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var failure = response.StatusCode switch {
                HttpStatusCode.Unauthorized => new ProviderFailure("authentication-failed", "Public.com authentication failed. Check or replace the server secret and restart."),
                HttpStatusCode.Forbidden => new ProviderFailure("permission-denied", "Public.com denied access. Verify the key has marketdata scope and account permissions."),
                HttpStatusCode.TooManyRequests => new ProviderFailure("rate-limited", "Public.com rate limit reached. Wait until the displayed retry time.",
                    RetryDelay(response)),
                HttpStatusCode.BadRequest => new ProviderFailure("request-rejected", "Public.com rejected the quote request. Verify account access and market-data permissions."),
                _ => new ProviderFailure("unavailable", "Public.com is unavailable or returned an unsupported response. Refresh to retry.")
            };
            throw failure;
        }
        const int limit = 128 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - _clock.GetUtcNow() : TimeSpan.FromMinutes(1));
        return delay < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : delay;
    }
    private sealed class ProviderFailure(string status, string message, TimeSpan? retryAfter = null) : Exception(message)
    {
        public string Status { get; } = status;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
