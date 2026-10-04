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
