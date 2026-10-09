using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutopilotQuant.Web;
using Xunit;

namespace AutopilotQuant.Tests;

public sealed class PublicMarketDataTests
{
    private const string Secret = "test-only-public-secret";
    private const string Token = "test-only-access-token";
    private const string Account = "BROKERAGE_TEST_1234";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-04T15:00:00Z");
    private static string Auth => JsonSerializer.Serialize(new { accessToken = Token });
    private static string Accounts => JsonSerializer.Serialize(new { accounts = new[] {
        new {accountId = "BOND_TEST", accountType = "BOND_ACCOUNT"},
        new {accountId = Account, accountType = "BROKERAGE"}
    }});
    private static object Quote(string symbol, string? last = "500.25", string? at = null, string outcome = "SUCCESS", string type = "EQUITY") => new {
        instrument = new {symbol, type}, outcome, last, lastTimestamp = at ?? Now.ToString("O"),
        bid = "500.20", bidTimestamp = Now.AddMinutes(-1).ToString("O"),
        ask = "500.30", askTimestamp = Now.ToString("O")
    };
    private static string Quotes(params object[] quotes) => JsonSerializer.Serialize(new { quotes });
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SecretExchangeAccountSelectionAndQuotesUseOnlyOfficialReadOnlyPaths()
    {
        using var handler = new Handler(Response(Auth), Response(Accounts), Response(Quotes(Quote("SPY"), Quote("QQQ"))));
        using var http = new HttpClient(handler);
        var client = new PublicMarketDataClient(http, Secret, clock: new Clock());
        var view = await client.RefreshAsync();
        Assert.Equal("snapshot", view.Status);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => { Assert.Equal("https", r.Uri.Scheme); Assert.Equal("api.public.com", r.Uri.Host); Assert.Empty(r.Uri.Query); });
        Assert.Equal("/userapiauthservice/personal/access-tokens", handler.Requests[0].Uri.AbsolutePath);
        Assert.Null(handler.Requests[0].Authorization);
        using var auth = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(Secret, auth.RootElement.GetProperty("secret").GetString());
        Assert.Equal(15, auth.RootElement.GetProperty("validityInMinutes").GetInt32());
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/userapigateway/trading/account", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal($"/userapigateway/marketdata/{Account}/quotes", handler.Requests[2].Uri.AbsolutePath);
        Assert.Equal("Bearer " + Token, handler.Requests[2].Authorization);
        using var body = JsonDocument.Parse(handler.Requests[2].Body!);
        var instruments = body.RootElement.GetProperty("instruments").EnumerateArray().ToArray();
        Assert.Equal(new[] {"SPY", "QQQ"}, instruments.Select(x => x.GetProperty("symbol").GetString()));
        Assert.All(instruments, i => Assert.Equal("EQUITY", i.GetProperty("type").GetString()));
        var serialized = JsonSerializer.Serialize(view);
        foreach (var sensitive in new[] {Secret, Token, Account}) Assert.DoesNotContain(sensitive, serialized);
        Assert.EndsWith("1234", view.AccountLabel);
        Assert.False(view.FuturesSupported); Assert.False(view.LiveRoutingEnabled);
    }

    [Fact]
    public async Task SourceTimesDrivePerPriceFreshnessAndAgeWithoutFurtherRequests()
    {
        var clock = new Clock();
        using var http = new HttpClient(new Handler(Response(Auth), Response(Accounts), Response(Quotes(Quote("SPY"), Quote("QQQ", at: Now.AddMinutes(1).ToString("O"))))));
        var client = new PublicMarketDataClient(http, Secret, clock: clock);
        var view = await client.RefreshAsync();
        Assert.True(view.Quotes![0].LastFresh);
        Assert.False(view.Quotes[0].BidFresh);
        Assert.True(view.Quotes[0].AskFresh);
        Assert.False(view.Quotes[1].LastFresh); // Future timestamps are not live.
        clock.Now = Now.AddSeconds(31);
        Assert.False(client.View().Quotes![0].LastFresh);
        Assert.False(client.View().Quotes![0].AskFresh);
    }

    [Fact]
    public async Task RefreshIsThrottledAndTokenIsReusedThenRenewed()
    {
        var clock = new Clock(); var quotes = Quotes(Quote("SPY"));
        using var handler = new Handler(Response(Auth), Response(Accounts), Response(quotes), Response(quotes), Response(Auth), Response(quotes));
        using var http = new HttpClient(handler);
        var client = new PublicMarketDataClient(http, Secret, clock: clock);
        await client.RefreshAsync(); await client.RefreshAsync();
        Assert.Equal(3, handler.Requests.Count);
        clock.Now = Now.AddSeconds(15); await client.RefreshAsync();
        Assert.Equal(4, handler.Requests.Count);
        clock.Now = Now.AddMinutes(14); await client.RefreshAsync();
        Assert.Equal(6, handler.Requests.Count);
        Assert.EndsWith("access-tokens", handler.Requests[4].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(401, "authentication-failed")]
    [InlineData(403, "permission-denied")]
    [InlineData(400, "request-rejected")]
    [InlineData(429, "rate-limited")]
    [InlineData(500, "unavailable")]
    [InlineData(302, "unavailable")]
    public async Task ProviderFailuresAreSanitizedAndNeverClaimQuotes(int status, string expected)
    {
        using var http = new HttpClient(new Handler(Response("DO_NOT_ECHO_" + Secret, (HttpStatusCode)status)));
        var view = await new PublicMarketDataClient(http, Secret).RefreshAsync();
        Assert.Equal(expected, view.Status); Assert.False(view.Refreshing); Assert.Empty(view.Quotes!);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task RateLimitRetryAfterPreventsEarlyOutboundRetry()
    {
        var clock = new Clock(); var limited = Response("ignored", HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
        using var handler = new Handler(limited);
        using var http = new HttpClient(handler);
        var client = new PublicMarketDataClient(http, Secret, clock: clock);
        Assert.Equal(Now.AddSeconds(90), (await client.RefreshAsync()).NextRequestAt);
        clock.Now = Now.AddSeconds(89); await client.RefreshAsync();
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("MES", "500.25", "2026-10-04T15:00:00Z", "EQUITY")]
    [InlineData("SPY", "-1", "2026-10-04T15:00:00Z", "EQUITY")]
    [InlineData("SPY", "NaN", "2026-10-04T15:00:00Z", "EQUITY")]
    [InlineData("SPY", "500.25", "2026-10-04T15:00:00", "EQUITY")]
    [InlineData("SPY", "500.25", "2026-10-04T15:00:00Z", "OPTION")]
    public async Task InvalidOrUnexpectedDataIsRejected(string symbol, string last, string at, string type)
    {
        using var http = new HttpClient(new Handler(Response(Auth), Response(Accounts), Response(Quotes(Quote(symbol, last, at, type: type)))));
        var view = await new PublicMarketDataClient(http, Secret).RefreshAsync();
        Assert.Equal("unavailable", view.Status); Assert.Empty(view.Quotes!);
    }

    [Fact]
    public async Task MissingAndUnsuccessfulSymbolsHaveNoInventedPrices()
    {
        using var http = new HttpClient(new Handler(Response(Auth), Response(Accounts), Response(Quotes(Quote("SPY", outcome: "NOT_FOUND")))));
        var view = await new PublicMarketDataClient(http, Secret).RefreshAsync();
        Assert.Equal("no-quotes", view.Status);
        Assert.Equal(2, view.Quotes!.Length);
        Assert.All(view.Quotes, q => { Assert.Equal("unavailable", q.Status); Assert.Null(q.Last); Assert.False(q.LastFresh); });
    }

    [Fact]
    public async Task MultipleBrokerageAccountsRequireExplicitSelection()
    {
        var multiple = JsonSerializer.Serialize(new {accounts = new[] {new {accountId="A", accountType="BROKERAGE"}, new {accountId="B", accountType="BROKERAGE"}}});
        using var handler = new Handler(Response(Auth), Response(multiple));
        using var http = new HttpClient(handler);
        Assert.Equal("account-required", (await new PublicMarketDataClient(http, Secret).RefreshAsync()).Status);
        Assert.Equal(2, handler.Requests.Count);
        using var selectedHandler = new Handler(Response(Auth), Response(multiple), Response(Quotes(Quote("SPY"))));
        using var selectedHttp = new HttpClient(selectedHandler);
        Assert.Equal("snapshot", (await new PublicMarketDataClient(selectedHttp, Secret, "B").RefreshAsync()).Status);
        Assert.EndsWith("/B/quotes", selectedHandler.Requests.Last().Uri.AbsolutePath);
    }

    [Fact]
    public async Task MissingSecretDoesNotConnectAndOversizedResponsesFailClosed()
    {
        using var handler = new Handler(Response(new string('x', 128 * 1024 + 1)));
        using var http = new HttpClient(handler);
        Assert.Equal("not-configured", (await new PublicMarketDataClient(http, "").RefreshAsync()).Status);
        Assert.Empty(handler.Requests);
        Assert.Equal("unavailable", (await new PublicMarketDataClient(http, Secret).RefreshAsync()).Status);
    }

    [Fact]
    public async Task FailedRefreshClearsPreviouslyReceivedPrices()
    {
        var clock = new Clock();
        using var http = new HttpClient(new Handler(Response(Auth), Response(Accounts), Response(Quotes(Quote("SPY"))), Response("denied", HttpStatusCode.Forbidden)));
        var client = new PublicMarketDataClient(http, Secret, clock: clock);
        Assert.NotEmpty((await client.RefreshAsync()).Quotes!);
        clock.Now = Now.AddSeconds(16);
        var failed = await client.RefreshAsync();
        Assert.Equal("permission-denied", failed.Status); Assert.Empty(failed.Quotes!);
    }

    private static string HistoryJson(string symbol = "SPY") => JsonSerializer.Serialize(new {
        symbol, period = "WEEK", regularMarket = new { bars = new[] {
            new { timestamp = "2026-10-04T10:55:00-04:00", open = "500.21", high = "500.31", low = "500.11", close = "500.25", volume = 123L },
            new { timestamp = "2026-10-04T10:50:00-04:00", open = "500.20", high = "500.30", low = "500.10", close = "500.21", volume = 120L }
        }}
    });

    [Fact]
    public async Task HistoryUsesAuthenticatedReadOnlyEndpointAndKeepsProviderTimestampsAndCents()
    {
        using var handler = new Handler(Response(Auth), Response(HistoryJson()));
        var client = new PublicMarketDataClient(new HttpClient(handler), Secret, clock: new Clock());
        var view = await client.RefreshHistoryAsync("SPY");
        Assert.Equal("available", view.Status); Assert.Equal(2, view.Bars.Length);
        Assert.Equal(DateTimeOffset.Parse("2026-10-04T14:50:00Z"), view.Bars[0].Timestamp);
        Assert.Equal(500.21m, view.Bars[0].Close);
        Assert.Equal(120, view.Bars[0].Volume);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.public.com/userapigateway/historicdata/EQUITY/SPY/WEEK/FIVE_MINUTES?tradingSessionToggle=REGULAR_HOURS", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + Token, request.Authorization);
        Assert.Equal(2, handler.Requests.Count); // No accounts, portfolio or order requests.
        foreach (var secret in new[] { Secret, Token, Account }) Assert.DoesNotContain(secret, JsonSerializer.Serialize(view));
        Assert.Empty(client.View().Quotes!);
        view.Bars[0] = view.Bars[0] with { Close = 1m };
        Assert.Equal(500.21m, client.History("SPY").Bars[0].Close);
    }

    [Fact]
    public async Task HistoryCachesEachSymbolForOneMinuteAndSharesTheTokenWithQuotes()
    {
        var clock = new Clock();
        using var handler = new Handler(Response(Auth), Response(HistoryJson()), Response(HistoryJson("QQQ")),
            Response(Accounts), Response(Quotes(Quote("SPY"))), Response(HistoryJson()));
        var client = new PublicMarketDataClient(new HttpClient(handler), Secret, clock: clock);
        await client.RefreshHistoryAsync("SPY"); await client.RefreshHistoryAsync("SPY");
        Assert.Equal(2, handler.Requests.Count);
        await client.RefreshHistoryAsync("QQQ"); await client.RefreshAsync();
        Assert.Equal(5, handler.Requests.Count);
        clock.Now = Now.AddSeconds(59); await client.RefreshHistoryAsync("SPY");
        Assert.Equal(5, handler.Requests.Count);
        clock.Now = Now.AddMinutes(1); await client.RefreshHistoryAsync("SPY");
        Assert.Equal(6, handler.Requests.Count);
        Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("access-tokens"));
    }

    [Theory]
    [InlineData("1324416.436999", "1324416.436999")]
    [InlineData("0", "0")]
    public async Task HistoryPreservesFractionalShareVolume(string providerVolume, string expected)
    {
        using var handler = new Handler(Response(Auth), Response(HistoryJson().Replace("\"volume\":123", "\"volume\":" + providerVolume)));
        var client = new PublicMarketDataClient(new HttpClient(handler), Secret, clock: new Clock());
        var view = await client.RefreshHistoryAsync("SPY");
        Assert.Equal("available", view.Status);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), view.Bars[1].Volume);
    }

    [Theory]
    [InlineData("SPY", "MES")]
    [InlineData("WEEK", "DAY")]
    [InlineData("10:55:00-04:00", "10:50:00-04:00")]
    [InlineData("10:55:00-04:00", "10:55:00")]
    [InlineData("10:55:00-04:00", "11:01:00-04:00")]
    [InlineData("500.25", "-1")]
    [InlineData("500.25", "501.00")]
    [InlineData("500.25", "NaN")]
    [InlineData("\"volume\":123", "\"volume\":-1")]
    [InlineData("\"volume\":123", "\"volume\":9223372036854775808")]
    [InlineData("\"volume\":123", "\"volume\":null")]
    public async Task InvalidHistoryNeverReachesTheChart(string from, string to)
    {
        using var handler = new Handler(Response(Auth), Response(HistoryJson().Replace(from, to)));
        var client = new PublicMarketDataClient(new HttpClient(handler), Secret, clock: new Clock());
        var view = await client.RefreshHistoryAsync("SPY");
        Assert.Equal("unavailable", view.Status); Assert.Empty(view.Bars);
        Assert.Null(view.FetchedAt);
    }

    [Fact]
    public async Task HistoryDoesNotInventMissingPricesOrCarryBarsAcrossFailedRefresh()
    {
        var clock = new Clock();
        using var handler = new Handler(Response(Auth), Response(HistoryJson()), Response("private-error-" + Secret, HttpStatusCode.Forbidden));
        var client = new PublicMarketDataClient(new HttpClient(handler), Secret, clock: clock);
        Assert.NotEmpty((await client.RefreshHistoryAsync("SPY")).Bars);
        clock.Now = Now.AddMinutes(1);
        var view = await client.RefreshHistoryAsync("SPY");
        Assert.Equal("permission-denied", view.Status); Assert.Empty(view.Bars);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task HistoryRateLimitsAlsoBlockQuoteRequests()
    {
        var limited = Response("ignored", HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
        using var handler = new Handler(Response(Auth), limited);
        var client = new PublicMarketDataClient(new HttpClient(handler), Secret, clock: new Clock());
        var view = await client.RefreshHistoryAsync("SPY");
        Assert.Equal("rate-limited", view.Status);
        Assert.Equal(Now.AddMinutes(2), view.NextRequestAt);
        Assert.Equal("rate-limited", (await client.RefreshAsync()).Status);
        Assert.Equal("rate-limited", (await client.RefreshHistoryAsync("QQQ")).Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HistoryRejectsUnknownSymbolsAndDoesNotConnectWithoutASecret()
    {
        using var handler = new Handler();
        var client = new PublicMarketDataClient(new HttpClient(handler), null);
        Assert.Equal("not-configured", (await client.RefreshHistoryAsync("SPY")).Status);
        foreach (var symbol in new[] { "MES", "MNQ", "spy", "../SPY", "QQQ?token=private" })
            await Assert.ThrowsAsync<ArgumentException>(() => client.RefreshHistoryAsync(symbol));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OversizedHistoryIsRejected()
    {
        using var handler = new Handler(Response(Auth), Response(new string('x', 2 * 1024 * 1024 + 1)));
        var client = new PublicMarketDataClient(new HttpClient(handler), Secret);
        Assert.Equal("unavailable", (await client.RefreshHistoryAsync("SPY")).Status);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = PublicMarketDataTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed record Request(Uri Uri, HttpMethod Method, string? Authorization, string? Body);
    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!, request.Method, request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            Assert.NotEmpty(_responses);
            return _responses.Dequeue();
        }
    }
}
