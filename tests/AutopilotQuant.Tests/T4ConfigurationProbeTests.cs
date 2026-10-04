using AutopilotQuant.T4;
using AutopilotQuant.T4.Protocol;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace AutopilotQuant.Tests;

public sealed class T4ConfigurationProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly T4Market[] Markets = [new("MES", "CME-test", "MES-test", "expiry-test")];
    private static ServerMessage Login(int entitlement = 1) => new() { LoginResponse = new() {
        SessionId = "test-session", Exchanges = { new LoginResponse.Types.Exchange { ExchangeId = "CME-test", MarketDataType = entitlement } }
    } };
    private static ServerMessage Details() => new() { MarketDetails = new() {
        MarketId = "expiry-test", ExchangeId = "CME-test", ContractId = "MES-test", ContractType = 5,
        MinPriceIncrement = new() { Value = "0.25" }, PointValue = new() { Value = "5" },
        LastTradingDate = Timestamp.FromDateTimeOffset(Now.AddMonths(2))
    } };
    private static ServerMessage Depth(bool delayed = false) => new() { MarketDepth = new() {
        MarketId = "expiry-test", Mode = 2, Delayed = delayed, Time = Timestamp.FromDateTimeOffset(Now),
        Bids = { new MarketDepth.Types.DepthLine { Price = new() { Value = "5000" }, Volume = 1 } },
        Offers = { new MarketDepth.Types.DepthLine { Price = new() { Value = "5000.25" }, Volume = 1 } }
    } };

    [Fact]
    public async Task Probe_Verifies_Contracts_And_Quotes_Then_Disposes_Without_Order_Messages()
    {
        var transport = new Transport(Login(), Details(), Depth());
        var result = await new T4ConfigurationProbe(() => transport, new Clock()).CheckAsync(new("x"), Markets, 15);
        Assert.Equal("verified", result.Outcome);
        Assert.True(result.Authenticated);
        Assert.True(Assert.Single(result.Markets).DefinitionVerified);
        Assert.True(result.Markets[0].FreshQuoteObserved);
        Assert.True(transport.Disposed);
        // Wire bytes independently assert envelope 2, API-key field 1, REAL format field 10.
        Assert.Equal(Convert.FromHexString("12050A01785001"), transport.Sent[0]);
        Assert.Equal(2, transport.Sent.Count);
        var subscribe = ClientMessage.Parser.ParseFrom(transport.Sent[1]).MarketSubscribe;
        Assert.Equal("expiry-test", subscribe.MarketId);
        Assert.Equal("MES-test", subscribe.ContractId);
        Assert.Equal(1, subscribe.Quotes);
        Assert.False(subscribe.Ticker);
    }

    [Fact]
    public async Task Password_Login_Uses_Official_Field_Numbers_And_Subscribes_Without_Orders()
    {
        var transport = new Transport(Login(), Details(), Depth());
        var credentials = new T4Credentials(firm: "f", username: "u", password: "p", appName: "a", appLicense: "l");
        var result = await new T4ConfigurationProbe(() => transport, new Clock()).CheckAsync(credentials, Markets, 15);
        Assert.Equal("verified", result.Outcome);
        // Independently assert envelope 2; firm/user/password/app/license fields 2–6; REAL field 10.
        Assert.Equal(Convert.FromHexString("12111201661A01752201702A016132016C5001"), transport.Sent[0]);
        Assert.Equal(2, transport.Sent.Count);
        Assert.NotNull(ClientMessage.Parser.ParseFrom(transport.Sent[1]).MarketSubscribe);
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData("Firm")] [InlineData("Username")] [InlineData("Password")]
    [InlineData("AppName")] [InlineData("AppLicense")]
    public async Task Partial_Login_Is_Blocked_Before_A_Transport_Is_Created(string missing)
    {
        var credentials = new T4Credentials(firm: missing == "Firm" ? " " : "f",
            username: missing == "Username" ? "" : "u", password: missing == "Password" ? null : "p",
            appName: missing == "AppName" ? "" : "a", appLicense: missing == "AppLicense" ? "" : "l");
        Assert.False(credentials.Configured);
        Assert.Equal([missing], credentials.MissingFields);
        var created = false;
        var probe = new T4ConfigurationProbe(() => { created = true; return new Transport(); });
        await Assert.ThrowsAsync<ArgumentException>(() => probe.CheckAsync(credentials, Markets, 15));
        Assert.False(created);
    }

    [Fact]
    public async Task ApiKey_Takes_Precedence_And_Rejected_Login_Does_Not_Fall_Back_To_Password()
    {
        var credentials = new T4Credentials("x", "f", "u", "p", "a", "l");
        var transport = new Transport(new ServerMessage { LoginResponse = new() { Result = 9 } });
        var result = await new T4ConfigurationProbe(() => transport).CheckAsync(credentials, Markets, 15);
        Assert.Equal("api-key", credentials.Method);
        Assert.Empty(credentials.MissingFields);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(Convert.FromHexString("12050A01785001"), Assert.Single(transport.Sent));
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(10)] [InlineData(999)]
    public async Task Missing_Delayed_Or_Unknown_Entitlement_Prevents_Subscription(int entitlement)
    {
        var transport = new Transport(Login(entitlement));
        var result = await new T4ConfigurationProbe(() => transport, new Clock()).CheckAsync(new("test"), Markets, 15);
        Assert.Equal("incomplete", result.Outcome);
        Assert.Single(transport.Sent);
        Assert.False(result.Markets[0].FreshQuoteObserved);
    }

    [Theory]
    [InlineData("point")] [InlineData("tick")] [InlineData("expiry")] [InlineData("product")] [InlineData("disabled")] [InlineData("spread")]
    public async Task Wrong_Or_Expired_Definition_Is_Not_Verified(string mismatch)
    {
        var details = Details();
        switch (mismatch)
        {
            case "point": details.MarketDetails.PointValue.Value = "50"; break;
            case "tick": details.MarketDetails.MinPriceIncrement.Value = "0.5"; break;
            case "expiry": details.MarketDetails.LastTradingDate = Timestamp.FromDateTimeOffset(Now.AddDays(-1)); break;
            case "product": details.MarketDetails.ContractId = "ES"; break;
            case "disabled": details.MarketDetails.Disabled = true; break;
            case "spread": details.MarketDetails.StrategyType = 1; break;
        }
        var result = await new T4ConfigurationProbe(() => new Transport(Login(), details, Depth()), new Clock()).CheckAsync(new("test"), Markets, 15);
        Assert.Equal("incomplete", result.Outcome);
        Assert.False(result.Markets[0].DefinitionVerified);
    }

    [Theory]
    [InlineData("delayed")] [InlineData("stale")] [InlineData("future")] [InlineData("closed")] [InlineData("one-side")]
    [InlineData("crossed")] [InlineData("tick")] [InlineData("recovery")]
    public async Task Unusable_Quote_Never_Passes_The_Configuration_Check(string kind)
    {
        var depth = Depth();
        switch (kind)
        {
            case "delayed": depth.MarketDepth.Delayed = true; break;
            case "stale": depth.MarketDepth.Time = Timestamp.FromDateTimeOffset(Now.AddSeconds(-16)); break;
            case "future": depth.MarketDepth.Time = Timestamp.FromDateTimeOffset(Now.AddSeconds(3)); break;
            case "closed": depth.MarketDepth.Mode = 5; break;
            case "one-side": depth.MarketDepth.Offers.Clear(); break;
            case "crossed": depth.MarketDepth.Offers[0].Price.Value = "4999.75"; break;
            case "tick": depth.MarketDepth.Offers[0].Price.Value = "5000.1"; break;
            case "recovery": depth.MarketDepth.Flags = 2048; break;
        }
        var result = await new T4ConfigurationProbe(() => new Transport(Login(), Details(), depth), new Clock()).CheckAsync(new("test"), Markets, 15);
        Assert.NotEqual("verified", result.Outcome);
        Assert.False(result.Markets[0].FreshQuoteObserved);
    }

    [Fact]
    public async Task Snapshot_Delay_Flag_Overrides_Inner_Quote_And_Login_Errors_Are_Sanitized()
    {
        var snapshot = new ServerMessage { MarketSnapshot = new() { MarketId = "expiry-test", Mode = 2, Delayed = true,
            Messages = { new MarketSnapshot.Types.SnapshotItem { MarketDepth = Depth().MarketDepth } } } };
        var result = await new T4ConfigurationProbe(() => new Transport(Login(), Details(), snapshot), new Clock()).CheckAsync(new("SECRET"), Markets, 15);
        Assert.False(result.Markets[0].FreshQuoteObserved);
        var failed = new ServerMessage { LoginResponse = new() { Result = 9 } };
        result = await new T4ConfigurationProbe(() => new Transport(failed), new Clock()).CheckAsync(new("SECRET"), Markets, 15);
        Assert.False(result.Authenticated);
        Assert.Equal("failed", result.Outcome);
        Assert.DoesNotContain("SECRET", result.Summary);
    }

    [Fact]
    public async Task No_Key_And_User_Cancellation_Do_Not_Produce_Verified_State()
    {
        var transport = new Transport();
        var probe = new T4ConfigurationProbe(() => transport, new Clock());
        await Assert.ThrowsAsync<ArgumentException>(() => probe.CheckAsync(new(""), Markets, 15));
        Assert.Empty(transport.Sent);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(new("test"), Markets, 15, cancellation.Token));
        Assert.True(transport.Disposed);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Transport(params ServerMessage[] messages) : IT4ProbeTransport
    {
        private readonly Queue<ServerMessage> _messages = new(messages);
        public List<byte[]> Sent { get; } = [];
        public bool Disposed { get; private set; }
        public Task ConnectAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task SendAsync(byte[] message, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Sent.Add(message); return Task.CompletedTask; }
        public Task<byte[]> ReceiveAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_messages.Count == 0) throw new OperationCanceledException("Simulated diagnostic deadline");
            return Task.FromResult(_messages.Dequeue().ToByteArray());
        }
        public void Dispose() => Disposed = true;
    }
}
