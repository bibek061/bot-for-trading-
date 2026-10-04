using System.Net;
using System.Text;
using System.Threading.Channels;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.T4;
using AutopilotQuant.T4.Protocol;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace AutopilotQuant.Tests;

public sealed class T4StreamingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 0, 1, TimeSpan.Zero);
    private static readonly T4Market Market = new("MES", "EX", "PRODUCT", "EXPIRY");
    private static MarketDetails Definition() => new() { MarketId = Market.MarketId, ExchangeId = Market.ExchangeId,
        ContractId = Market.ProductId, ContractType = 5, MinPriceIncrement = new() { Value = ".25" }, PointValue = new() { Value = "5" },
        LastTradingDate = Timestamp.FromDateTimeOffset(Now.AddMonths(2)) };
    private static MarketDepth Depth(DateTimeOffset at) => new() { MarketId = Market.MarketId, Mode = 2, Time = Timestamp.FromDateTimeOffset(at),
        Bids = { new MarketDepth.Types.DepthLine { Price = new() { Value = "5000" }, Volume = 1 } },
        Offers = { new MarketDepth.Types.DepthLine { Price = new() { Value = "5000.25" }, Volume = 1 } } };
    private static MarketTrade Trade(DateTimeOffset at, int count, string price = "5000") => new() { MarketId = Market.MarketId,
        Mode = 2, Time = Timestamp.FromDateTimeOffset(at), LastTradePrice = new() { Value = price }, LastTradeVolume = 2, TotalTradeCount = count };
    private static T4MarketAccumulator Accumulator()
    {
        var accumulator = new T4MarketAccumulator(Market, Now); accumulator.Verify(Definition(), Now); accumulator.BeginSnapshot(Now); return accumulator;
    }

    [Fact]
    public void Snapshot_And_Contract_Are_Required_Before_Quotes_Then_Incremental_Sides_Update()
    {
        var accumulator = new T4MarketAccumulator(Market, Now);
        Assert.Null(accumulator.Depth(Depth(Now), Now, 15));
        accumulator.Verify(Definition(), Now);
        Assert.Null(accumulator.Depth(Depth(Now), Now, 15));
        accumulator.BeginSnapshot(Now);
        Assert.Equal(5000.25m, accumulator.Depth(Depth(Now), Now, 15)!.Ask);
        var update = Depth(Now.AddSeconds(1)); update.Offers.Clear(); update.Bids[0].Price.Value = "4999.75";
        var quote = accumulator.Depth(update, Now.AddSeconds(1), 15)!;
        Assert.Equal(4999.75m, quote.Bid); Assert.Equal(5000.25m, quote.Ask); Assert.Equal(Now.AddSeconds(1), quote.Timestamp);
        update = Depth(Now.AddSeconds(2)); update.Bids[0].Volume = 0; update.Offers.Clear();
        Assert.Null(accumulator.Depth(update, Now.AddSeconds(2), 15));
    }

    [Theory]
    [InlineData("stale")] [InlineData("future")] [InlineData("delayed")] [InlineData("crossed")] [InlineData("reordered")]
    public void Bad_Depth_Stops_The_Feed(string kind)
    {
        var accumulator = Accumulator(); accumulator.Depth(Depth(Now), Now, 15);
        var update = Depth(Now.AddSeconds(1));
        var now = Now.AddSeconds(1);
        switch (kind)
        {
            case "stale": now = Now.AddSeconds(30); break;
            case "future": update.Time = Timestamp.FromDateTimeOffset(Now.AddSeconds(9)); break;
            case "delayed": update.Delayed = true; break;
            case "crossed": update.Offers[0].Price.Value = "4999"; break;
            case "reordered": update.Time = Timestamp.FromDateTimeOffset(Now.AddSeconds(-1)); break;
        }
        Assert.Throws<T4FeedException>(() => accumulator.Depth(update, now, 15));
    }

    [Fact]
    public void First_Partial_Interval_Is_Discarded_And_Only_Complete_Bars_Are_Emitted()
    {
        var accumulator = Accumulator();
        Assert.Null(accumulator.Trade(Trade(Now, 10), Now, 15));
        var firstComplete = T4MarketAccumulator.Floor(Now).AddMinutes(5);
        Assert.Null(accumulator.Trade(Trade(firstComplete, 11, "5001"), firstComplete, 15));
        Assert.Null(accumulator.Trade(Trade(firstComplete.AddMinutes(1), 12, "5002"), firstComplete.AddMinutes(1), 15));
        Assert.Null(accumulator.Trade(Trade(firstComplete.AddMinutes(1), 12, "5002"), firstComplete.AddMinutes(1), 15));
        var end = firstComplete.AddMinutes(5);
        var bar = accumulator.Trade(Trade(end, 13, "5003"), end, 15)!;
        Assert.Equal(end, bar.Timestamp); Assert.Equal(5001m, bar.Open); Assert.Equal(5002m, bar.High); Assert.Equal(5002m, bar.Close);
        Assert.Equal(4, bar.Volume); Assert.Equal(end.AddMinutes(5), accumulator.FormingBar!.Timestamp);
        Assert.Throws<T4FeedException>(() => accumulator.Trade(Trade(end.AddSeconds(1), 15), end.AddSeconds(1), 15));
    }

    [Fact]
    public void Reset_Discards_Forming_Candle_And_Requires_A_New_Snapshot()
    {
        var accumulator = Accumulator(); accumulator.Trade(Trade(Now, 10), Now, 15);
        accumulator.Reset(Now.AddSeconds(1));
        Assert.Null(accumulator.FormingBar); Assert.Null(accumulator.Depth(Depth(Now.AddSeconds(1)), Now.AddSeconds(1), 15));
    }

    private const string HistoryJson = """
        {"marketDefinitions":[{"marketID":"EXPIRY","minPriceIncrement":"25","tickValue":1.25}],
         "bars":[{"marketID":"EXPIRY","time":"2026-10-05T07:50:00","closeTime":"2026-10-05T07:54:59.123",
         "openPrice":"500000","highPrice":"500100","lowPrice":"499900","closePrice":"500025","volume":12}]}
        """;

    [Fact]
    public void History_Uses_Defined_Price_Scale_And_Interval_End_Not_Last_Trade_Time()
    {
        var bar = Assert.Single(T4HistoryClient.Parse(Encoding.UTF8.GetBytes(HistoryJson), Market, "CST", Now));
        Assert.Equal(new DateTimeOffset(2026,10,5,13,55,0,TimeSpan.Zero),bar.Timestamp);
        Assert.Equal(5000m,bar.Open); Assert.Equal(5000.25m,bar.Close);
        var daylight = T4HistoryClient.Parse(Encoding.UTF8.GetBytes(HistoryJson), Market, "America/Chicago", Now)[0];
        Assert.Equal(bar.Timestamp.AddHours(-1),daylight.Timestamp);
    }

    [Theory]
    [InlineData("\"marketID\":\"EXPIRY\",\"time\"", "\"marketID\":\"OTHER\",\"time\"")]
    [InlineData("\"tickValue\":1.25", "\"tickValue\":12.5")]
    [InlineData("07:50:00", "07:51:00")]
    [InlineData("500025", "500026")]
    [InlineData("\"volume\":12", "\"volume\":-1")]
    public void Invalid_History_Is_Rejected(string from, string to) =>
        Assert.Throws<T4FeedException>(() => T4HistoryClient.Parse(Encoding.UTF8.GetBytes(HistoryJson.Replace(from,to)), Market,"CST",Now));

    [Fact]
    public void Ambiguous_And_Invalid_DST_Timestamps_Are_Rejected()
    {
        Assert.Throws<T4FeedException>(() => T4HistoryClient.Timestamp("2026-11-01T01:30:00","America/Chicago"));
        Assert.Throws<T4FeedException>(() => T4HistoryClient.Timestamp("2026-03-08T02:30:00","America/Chicago"));
        Assert.Equal(new DateTimeOffset(2026,11,1,7,30,0,TimeSpan.Zero),T4HistoryClient.Timestamp("2026-11-01T01:30:00-06:00","America/Chicago"));
    }

    [Theory]
    [InlineData("unconfirmed", false)] [InlineData("CST", false)]
    [InlineData("unconfirmed", true)] [InlineData("CST", true)]
    public async Task Continuous_Client_Subscribes_Loads_Optional_History_And_Disposes_On_Disconnect(string timezone, bool passwordLogin)
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new Sink(cancellation);
        var transport = new ScriptedTransport([
            new() { LoginResponse = new() { SessionId="session", Exchanges = { new LoginResponse.Types.Exchange {ExchangeId="EX",MarketDataType=1} },
                AuthenticationToken = new() { Token="TEST-REST-TOKEN",ExpireTime=Timestamp.FromDateTimeOffset(Now.AddHours(1)) } } },
            new() { MarketDetails=Definition() },
            new() { MarketSnapshot=new() {MarketId="EXPIRY",Mode=2,Messages={ new MarketSnapshot.Types.SnapshotItem {MarketDepth=Depth(Now)} }} }
        ]);
        var handler = new HistoryHandler();
        var client = new T4StreamingClient(new(new HttpClient(handler)), () => transport, new Clock());
        var credentials = passwordLogin ? new T4Credentials(firm: "test-firm", username: "test-user",
            password: " test-password ", appName: "test-app", appLicense: "test-license") : new("TEST-API-KEY");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RunAsync(credentials,[Market],timezone,15,sink,cancellation.Token));
        var login = transport.Sent[0].LoginRequest;
        Assert.Equal(passwordLogin ? "" : "TEST-API-KEY", login.ApiKey);
        Assert.Equal(passwordLogin ? " test-password " : "", login.Password);
        Assert.Equal(passwordLogin ? "test-firm" : "", login.Firm);
        Assert.Equal(passwordLogin ? "test-user" : "", login.Username);
        Assert.Equal(passwordLogin ? "test-app" : "", login.AppName);
        Assert.Equal(passwordLogin ? "test-license" : "", login.AppLicense);
        Assert.Equal(1, login.PriceFormat);
        Assert.Single(sink.Quotes); Assert.True(transport.Disposed);
        Assert.Contains(transport.Sent,m => m.MarketSubscribe?.Ticker == true);
        Assert.Equal(timezone == "CST" ? 1 : 0,handler.Calls);
        Assert.Equal(timezone == "CST" ? 1 : 0,sink.Bars.Count);
        Assert.All(sink.Bars,b => Assert.True(b.Warmup));
    }

    [Fact]
    public async Task Incomplete_Password_Credentials_Cannot_Start_A_Stream()
    {
        var created = false;
        using var cancellation = new CancellationTokenSource();
        var client = new T4StreamingClient(new(new HttpClient(new HistoryHandler())), () => {
            created = true; return new ScriptedTransport([]);
        }, new Clock());
        var error = await Assert.ThrowsAsync<T4FeedException>(() => client.RunAsync(new(firm: "f", username: "u", password: "p"),
            [Market], "CST", 15, new Sink(cancellation), cancellation.Token));
        Assert.False(error.Retryable);
        Assert.False(created);
    }

    [Fact]
    public async Task Completed_API_Bars_Repair_Startup_Interval_Without_Blocking_Quotes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new MovingClock();
        var transport = new InteractiveTransport(clock);
        var handler = new RefreshHandler();
        var sink = new RefreshSink(clock,cancellation);
        var client = new T4StreamingClient(new(new HttpClient(handler)),() => transport,clock);
        var run = client.RunAsync(new("TEST-API-KEY"),[Market],"CST",15,sink,cancellation.Token);
        try
        {
            await handler.RefreshStarted.Task.WaitAsync(cancellation.Token);
            clock.Now = Now.AddMinutes(5).AddSeconds(1);
            transport.Publish(new() {MarketDepth=Depth(clock.Now)});
            await sink.QuoteWhileRefreshing.Task.WaitAsync(cancellation.Token);
            Assert.False(run.IsCompleted); // Historical HTTP is still pending; incoming quotes were consumed.
            handler.CompleteRefresh();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            var bar = Assert.Single(sink.Completed);
            Assert.Equal(T4MarketAccumulator.Floor(Now).AddMinutes(5),bar.Bar.Timestamp);
            Assert.False(bar.Warmup);
            Assert.Equal(12,bar.Bar.Volume);
        }
        finally
        {
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    private sealed class MovingClock : TimeProvider
    {
        public DateTimeOffset Now = T4StreamingTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class InteractiveTransport(MovingClock clock) : IT4ProbeTransport
    {
        private readonly Channel<ServerMessage> _frames = Channel.CreateUnbounded<ServerMessage>();
        public void Publish(ServerMessage frame) => _frames.Writer.TryWrite(frame);
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(byte[] bytes,CancellationToken ct)
        {
            var message = ClientMessage.Parser.ParseFrom(bytes);
            AuthenticationToken Token(string id) => new() { RequestId=id,Token="TEST-REST-TOKEN",ExpireTime=Timestamp.FromDateTimeOffset(clock.Now.AddHours(1)) };
            if (message.LoginRequest is not null) Publish(new() {LoginResponse=new() {SessionId="session",AuthenticationToken=Token("login"),
                Exchanges={new LoginResponse.Types.Exchange {ExchangeId="EX",MarketDataType=1}}}});
            if (message.MarketSubscribe is not null)
            {
                Publish(new() {MarketDetails=Definition()});
                Publish(new() {MarketSnapshot=new() {MarketId="EXPIRY",Mode=2,Messages={new MarketSnapshot.Types.SnapshotItem {MarketDepth=Depth(clock.Now)}}}});
            }
            if (message.AuthenticationTokenRequest is { } request) Publish(new() {AuthenticationToken=Token(request.RequestId)});
            return Task.CompletedTask;
        }
        public async Task<byte[]> ReceiveAsync(CancellationToken ct) => (await _frames.Reader.ReadAsync(ct)).ToByteArray();
        public void Dispose() { _frames.Writer.TryComplete(); }
    }
    private sealed class RefreshHandler : HttpMessageHandler
    {
        private int _calls;
        public TaskCompletionSource RefreshStarted {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) return Task.FromResult(Response("07:55:00","07:59:59.123"));
            RefreshStarted.TrySetResult(); return _response.Task.WaitAsync(ct);
        }
        public void CompleteRefresh() => _response.TrySetResult(Response("08:00:00","08:04:59.123"));
        private static HttpResponseMessage Response(string start,string close) => new(HttpStatusCode.OK) {
            Content=new StringContent(HistoryJson.Replace("07:50:00",start).Replace("07:54:59.123",close),Encoding.UTF8,"application/json")
        };
    }
    private sealed class RefreshSink(MovingClock clock,CancellationTokenSource cancellation) : IT4FeedSink
    {
        private int _quotes;
        public TaskCompletionSource QuoteWhileRefreshing {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FeedBar> Completed {get;}=[];
        public void State(string stage,string message) { }
        public void Received() { }
        public Task QuoteAsync(FeedQuote quote)
        {
            if (++_quotes == 1) clock.Now = Now.AddMinutes(5);
            else QuoteWhileRefreshing.TrySetResult();
            return Task.CompletedTask;
        }
        public Task BarAsync(FeedBar bar) {Completed.Add(bar); cancellation.Cancel(); return Task.CompletedTask;}
        public Task HistoryAsync(IReadOnlyList<FeedBar> bars) => Task.CompletedTask;
        public void Forming(MarketBar bar) { }
        public Task InvalidateAsync(string reason) => Task.CompletedTask;
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class HistoryHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal("api-sim.t4login.com",request.RequestUri!.Host);
            Assert.Contains("marketID=EXPIRY",request.RequestUri.Query);
            Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(HistoryJson,Encoding.UTF8,"application/json")});
        }
    }
    private sealed class ScriptedTransport(ServerMessage[] messages) : IT4ProbeTransport
    {
        private readonly Queue<ServerMessage> _frames = new(messages);
        public bool Disposed; public List<ClientMessage> Sent { get; }=[];
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(byte[] message,CancellationToken ct) { Sent.Add(ClientMessage.Parser.ParseFrom(message)); return Task.CompletedTask; }
        public async Task<byte[]> ReceiveAsync(CancellationToken ct)
        {
            if (_frames.Count > 0) return _frames.Dequeue().ToByteArray();
            await Task.Delay(Timeout.Infinite,ct); throw new OperationCanceledException();
        }
        public void Dispose() => Disposed=true;
    }
    private sealed class Sink(CancellationTokenSource cancellation) : IT4FeedSink
    {
        public List<FeedQuote> Quotes { get; }=[]; public List<FeedBar> Bars { get; }=[];
        public void State(string stage,string message) { }
        public void Received() { }
        public Task QuoteAsync(FeedQuote quote) { Quotes.Add(quote); cancellation.Cancel(); return Task.CompletedTask; }
        public Task BarAsync(FeedBar bar) { Bars.Add(bar); return Task.CompletedTask; }
        public Task HistoryAsync(IReadOnlyList<FeedBar> bars) { Bars.AddRange(bars); return Task.CompletedTask; }
        public void Forming(MarketBar bar) { }
        public Task InvalidateAsync(string reason) => Task.CompletedTask;
    }
}
