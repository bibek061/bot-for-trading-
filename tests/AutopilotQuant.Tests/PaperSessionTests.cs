using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.MarketData;
using Xunit;

namespace AutopilotQuant.Tests;

public sealed class PaperSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AutopilotQuantTests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private PaperSessionStore _store;
    private PaperSession _session;
    public PaperSessionTests()
    {
        _store = new(_directory);
        _session = new(_store, _clock);
    }
    public void Dispose()
    {
        _store.Dispose();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutopilotQuantTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_directory, true);
    }
    private FeedQuote Quote(decimal bid = 123.25m, string contract = "MES-test-contract") =>
        new("MES", contract, _clock.Now, bid, bid + .25m);
    private List<MarketBar> SignalBars()
    {
        var start = _clock.Now.AddMinutes(-500);
        var prices = Enumerable.Range(0, 98).Select(i => 100m + i * .25m).Concat([122.25m, 120.25m, 123.25m]);
        return prices.Select((close, i) => new MarketBar("MES", start.AddMinutes(i * 5), close, close + .25m, close - .25m, close, 100)).ToList();
    }
    private async Task QueueSignal()
    {
        var bars = SignalBars();
        foreach (var bar in bars.Take(100)) await _session.AcceptBarAsync(new("MES-test-contract", bar, true));
        await _session.AcceptQuoteAsync(Quote());
        await _session.ControlAsync("resume");
        await _session.AcceptBarAsync(new("MES-test-contract", bars[^1]));
    }
    private async Task<SessionView> OpenPosition()
    {
        await QueueSignal();
        _clock.Now = _clock.Now.AddSeconds(1);
        return await _session.AcceptQuoteAsync(Quote());
    }

    [Fact]
    public async Task Starts_Paused_And_Cannot_Resume_Without_Fresh_Quotes()
    {
        Assert.True((await _session.ViewAsync()).Paused);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ControlAsync("resume"));
    }

    [Fact]
    public async Task Feed_Configuration_Cannot_Clear_Open_Exposure_Or_Armed_Strategy()
    {
        await QueueSignal();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ResetMarketDataAsync());
        _clock.Now = _clock.Now.AddSeconds(1);
        await _session.AcceptQuoteAsync(Quote());
        await _session.ControlAsync("pause");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ResetMarketDataAsync());
        var view = await _session.ViewAsync();
        Assert.Single(view.Positions);
        Assert.Single(view.Protection);
        Assert.True(view.Instruments[0].Fresh);
    }

    [Fact]
    public async Task Disconnect_Invalidates_Quotes_But_Preserves_Exposure_And_Protection()
    {
        await OpenPosition();
        var paused = await _session.InvalidateMarketDataAsync("Transport disconnected");
        Assert.True(paused.Paused);
        Assert.Single(paused.Positions); Assert.Single(paused.Protection);
        Assert.Empty(paused.Pending);
        Assert.All(paused.Instruments, i => { Assert.Null(i.Quote); Assert.Empty(i.Bars); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ControlAsync("resume"));
        await _session.ControlAsync("flatten");
        Assert.Single((await _session.ViewAsync()).Positions);
        _clock.Now = _clock.Now.AddSeconds(1);
        var closed = await _session.AcceptQuoteAsync(Quote(117m));
        Assert.Empty(closed.Positions); Assert.Single(closed.Trades);
    }

    [Fact]
    public async Task Quote_Batch_Evaluates_Intermediate_Stop_And_Does_Not_Only_Use_Last_Price()
    {
        await QueueSignal();
        _clock.Now = _clock.Now.AddSeconds(1); var entry = Quote();
        _clock.Now = _clock.Now.AddSeconds(1); var stop = Quote(117m);
        _clock.Now = _clock.Now.AddSeconds(1); var rebound = Quote(130m);
        var result = await _session.AcceptQuotesAsync([entry,stop,rebound]);
        Assert.Empty(result.Positions);
        Assert.Equal(116.75m,Assert.Single(result.Trades).ExitPrice);
        Assert.Equal(2,result.Fills.Count);
        Assert.Equal(130m,result.Instruments[0].Quote!.Bid);
    }

    [Fact]
    public async Task Restart_Does_Not_Treat_A_Persisted_Clock_Skewed_Quote_As_New_Data()
    {
        await _session.AcceptQuoteAsync(Quote() with { Timestamp = _clock.Now.AddSeconds(1) });
        _clock.Now = _clock.Now.AddMilliseconds(100);
        _store.Dispose(); _store = new(_directory); _session = new(_store,_clock);
        Assert.Null((await _session.ViewAsync()).Instruments[0].Quote);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ControlAsync("resume"));
    }

    [Fact]
    public async Task Signal_Fills_Only_On_A_Later_Quote_At_Ask_Plus_Slippage()
    {
        await QueueSignal();
        var pending = await _session.ViewAsync();
        Assert.Empty(pending.Positions);
        Assert.Single(pending.Pending);
        _clock.Now = _clock.Now.AddSeconds(1);
        var filled = await _session.AcceptQuoteAsync(Quote());
        var position = Assert.Single(filled.Positions);
        Assert.Equal(123.75m, position.EntryPrice);
        Assert.Equal(-2.5m, filled.UnrealizedGross);
        Assert.Equal(118.75m, Assert.Single(filled.Protection).StopPrice);
        Assert.Empty(filled.Pending);
        Assert.Single(filled.Fills);
    }

    [Fact]
    public async Task Duplicate_Quote_And_Bar_Do_Not_Duplicate_Orders()
    {
        var bars = SignalBars();
        await OpenPosition();
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptQuoteAsync(Quote()));
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptBarAsync(new("MES-test-contract", bars[^1])));
        Assert.Single((await _session.ViewAsync()).Fills);
    }

    [Fact]
    public async Task Pause_Retains_Protection_And_A_Gap_Stop_Uses_Actual_Bid()
    {
        await OpenPosition();
        await _session.ControlAsync("pause");
        _clock.Now = _clock.Now.AddSeconds(1);
        var stopped = await _session.AcceptQuoteAsync(Quote(117m));
        Assert.Empty(stopped.Positions);
        Assert.Empty(stopped.Protection);
        Assert.Equal(116.75m, Assert.Single(stopped.Trades).ExitPrice);
        Assert.True(stopped.Paused);
    }

    [Fact]
    public async Task Stale_Flatten_Waits_For_Fresh_Quote_And_Closes_Exactly_Once()
    {
        await OpenPosition();
        _clock.Now = _clock.Now.AddSeconds(30);
        await _session.MonitorAsync();
        var pending = await _session.ControlAsync("flatten");
        Assert.True(pending.Paused);
        Assert.Single(pending.Positions);
        Assert.Empty(pending.Trades);
        _clock.Now = _clock.Now.AddSeconds(1);
        var closed = await _session.AcceptQuoteAsync(Quote(123m));
        Assert.Empty(closed.Positions);
        Assert.Single(closed.Trades);
        await _session.ControlAsync("flatten");
        Assert.Single((await _session.ViewAsync()).Trades);
    }

    [Fact]
    public async Task Restart_Preserves_Exposure_And_Pauses_With_No_Pending_Entry()
    {
        var opened = await OpenPosition();
        _clock.Now = _clock.Now.AddSeconds(2);
        _store.Dispose();
        _store = new(_directory); _session = new(_store, _clock);
        var restored = await _session.ViewAsync();
        Assert.True(restored.Paused);
        Assert.Equal(opened.Equity, restored.Equity);
        Assert.Equal(opened.Positions[0].PositionId, Assert.Single(restored.Positions).PositionId);
        Assert.Single(restored.Protection);
        Assert.Empty(restored.Pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ControlAsync("resume"));
    }

    [Fact]
    public async Task Restart_Drops_Unfilled_Signal_And_Preserves_Processed_Bar()
    {
        var bars = SignalBars(); await QueueSignal();
        _store.Dispose(); _clock.Now = _clock.Now.AddSeconds(1);
        _store = new(_directory); _session = new(_store, _clock);
        Assert.Empty((await _session.ViewAsync()).Pending);
        await _session.AcceptQuoteAsync(Quote());
        await _session.ControlAsync("resume");
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptBarAsync(new("MES-test-contract", bars[^1])));
        Assert.Empty((await _session.ViewAsync()).Fills);
    }

    [Fact]
    public async Task Daily_Loss_Monitors_Open_Exposure_And_Flatten_Halt_Persists()
    {
        await _session.ConfigureAsync(new(DailyLossLimitPct: .001m));
        await OpenPosition();
        _clock.Now = _clock.Now.AddSeconds(1);
        var view = await _session.AcceptQuoteAsync(Quote(120m));
        Assert.True(view.RiskHalted);
        Assert.True(view.Paused);
        Assert.Empty(view.Positions);
        Assert.Single(view.Trades);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ControlAsync("resume"));
        _store.Dispose(); _store = new(_directory); _session = new(_store, _clock);
        Assert.True((await _session.ViewAsync()).RiskHalted);
    }

    [Fact]
    public async Task PerTrade_Risk_Budget_Rejects_Entry()
    {
        await _session.ConfigureAsync(new(RiskPerTradePct: .0001m));
        await QueueSignal(); _clock.Now = _clock.Now.AddSeconds(1);
        var result = await _session.AcceptQuoteAsync(Quote());
        Assert.Empty(result.Positions);
        Assert.Contains(result.Events, e => e.Message.Contains("per-trade budget"));
    }

    [Fact]
    public async Task Excessive_Spread_Rejects_Entry()
    {
        await QueueSignal(); _clock.Now = _clock.Now.AddSeconds(1);
        var result = await _session.AcceptQuoteAsync(Quote() with { Ask = 125m });
        Assert.Empty(result.Positions);
        Assert.Contains(result.Events, e => e.Message.Contains("Spread limit"));
    }

    [Fact]
    public async Task Contract_Change_Is_Rejected_While_Position_Open()
    {
        await OpenPosition(); await _session.ControlAsync("pause");
        _clock.Now = _clock.Now.AddSeconds(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.AcceptQuoteAsync(Quote(contract: "next-contract")));
        Assert.Single((await _session.ViewAsync()).Positions);
    }

    [Fact]
    public async Task Settings_Cannot_Reset_Account_Or_Change_While_Exposed()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _session.ConfigureAsync(new(InitialEquity: 20000)));
        await OpenPosition(); await _session.ControlAsync("pause");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ConfigureAsync(new(StopTicks: 30)));
    }

    [Fact]
    public async Task Session_Risk_Reset_Is_At_18ET_Not_Midnight_And_Dst_Aware()
    {
        await _session.AcceptQuoteAsync(Quote()); await _session.ControlAsync("resume");
        var before = _store.Load().SessionDate;
        _clock.Now = new(2026, 10, 5, 21, 59, 0, TimeSpan.Zero); await _session.MonitorAsync();
        Assert.Equal(before, _store.Load().SessionDate);
        _clock.Now = _clock.Now.AddMinutes(1); await _session.MonitorAsync();
        Assert.Equal(before!.Value.AddDays(1), _store.Load().SessionDate);
        _clock.Now = new(2026, 10, 6, 4, 0, 0, TimeSpan.Zero); await _session.MonitorAsync();
        Assert.Equal(before.Value.AddDays(1), _store.Load().SessionDate);
        _clock.Now = new(2026, 11, 2, 22, 59, 0, TimeSpan.Zero); await _session.MonitorAsync();
        var winterBefore = _store.Load().SessionDate;
        _clock.Now = _clock.Now.AddMinutes(1); await _session.MonitorAsync();
        Assert.Equal(winterBefore!.Value.AddDays(1), _store.Load().SessionDate);
    }

    [Fact]
    public async Task Malformed_Or_Historical_Quote_Does_Not_Mutate_State()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptQuoteAsync(Quote() with { Ask = 100m }));
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptQuoteAsync(Quote() with { Timestamp = _clock.Now.AddMinutes(-5) }));
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptQuoteAsync(Quote() with { Bid = 123.1m }));
        Assert.All((await _session.ViewAsync()).Instruments, i => Assert.Null(i.Quote));
    }

    [Fact]
    public async Task Stop_Already_Crossed_At_Entry_Closes_In_The_Same_Transaction()
    {
        await _session.ConfigureAsync(new(StopTicks: 1));
        var view = await OpenPosition();
        Assert.Empty(view.Positions);
        Assert.Empty(view.Protection);
        Assert.Single(view.Trades);
        Assert.Equal(2, view.Fills.Count);
    }

    [Fact]
    public async Task Failed_Persistence_Does_Not_Publish_A_Fill_And_Blocks_Further_Mutations()
    {
        await QueueSignal();
        // Force the snapshot write to fail without changing the committed primary.
        Directory.CreateDirectory(Path.Combine(_directory, "paper-session.json.tmp"));
        _clock.Now = _clock.Now.AddSeconds(1);
        var error = await Record.ExceptionAsync(() => _session.AcceptQuoteAsync(Quote()));
        Assert.True(error is IOException or UnauthorizedAccessException);
        var view = await _session.ViewAsync();
        Assert.Empty(view.Fills);
        Assert.True(view.Paused);
        Assert.Contains("Storage fault", view.PauseReason);
        Assert.Empty(_store.Load().Broker.Fills);
        await Assert.ThrowsAsync<IOException>(() => _session.ControlAsync("resume"));
    }

    [Fact]
    public async Task SubMillisecond_And_Unmarked_Historical_Bars_Are_Rejected()
    {
        var bar = SignalBars()[^1];
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptBarAsync(new("MES-test-contract", bar with { Timestamp = bar.Timestamp.AddTicks(-1) }, true)));
        await Assert.ThrowsAsync<ArgumentException>(() => _session.AcceptBarAsync(new("MES-test-contract", bar with { Timestamp = bar.Timestamp.AddMinutes(-5) })));
        Assert.Empty((await _session.ViewAsync()).Instruments[0].Bars);
    }

    [Fact]
    public void Second_Writer_And_Corrupt_State_Fail_Closed()
    {
        Assert.Throws<IOException>(() => new PaperSessionStore(_directory));
        _store.Dispose();
        File.WriteAllText(Path.Combine(_directory, "paper-session.json"), "not-json");
        _store = new(_directory);
        Assert.Throws<System.Text.Json.JsonException>(() => new PaperSession(_store, _clock));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
