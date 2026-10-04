using System.Globalization;
using System.Text.Json;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Replay;
using AutopilotQuant.Core.Risk;
using Xunit;

namespace AutopilotQuant.Tests;

public class HistoricalReplayRunnerTests
{
    private static IReadOnlyList<MarketBar> CreateReplayBars()
    {
        var bars = new List<MarketBar>();
        for (var i = 0; i < 98; i++)
            bars.Add(CreateBar(i, 100m + i * 0.25m));
        bars.Add(CreateBar(98, 122.25m));
        bars.Add(CreateBar(99, 120.25m));
        bars.Add(CreateBar(100, 123.25m));
        bars.Add(CreateBar(101, 124.5m));
        return bars;
    }

    private static IReadOnlyList<MarketBar> CreateDayBars(DateTimeOffset start, bool endWithLoss)
    {
        var bars = new List<MarketBar>();
        for (var i = 0; i < 98; i++)
            bars.Add(CreateBar(start, i, 100m + i * 0.25m));
        bars.Add(CreateBar(start, 98, 122.25m));
        bars.Add(CreateBar(start, 99, 120.25m));
        bars.Add(CreateBar(start, 100, 123.25m));
        if (endWithLoss)
            bars.Add(CreateBar(start, 101, 100.25m));
        return bars;
    }

    private static MarketBar CreateBar(int index, decimal close) =>
        CreateBar(new DateTimeOffset(2025, 1, 2, 9, 30, 0, TimeSpan.FromHours(-5)), index, close);

    private static MarketBar CreateBar(DateTimeOffset start, int index, decimal close) =>
        new(
            "MES",
            start.AddMinutes(index * 5),
            close,
            close + 0.25m,
            close - 0.25m,
            close,
            100);

    [Fact]
    public async Task Replay_Processes_Completed_Bars_And_Reports_Fees_And_Results()
    {
        var runner = new HistoricalReplayRunner(new HistoricalReplayOptions(
            InitialEquity: 10_000m,
            FeePerContract: 1m,
            SlippageTicks: 1));

        var report = await runner.RunAsync(CreateReplayBars());

        Assert.Equal(102, report.BarsProcessed);
        Assert.Equal(1, report.SignalCandidates);
        Assert.Equal(1, report.OrdersPlaced);
        Assert.Equal(0, report.RiskRejected);
        Assert.Equal(1, report.TradesClosed);
        Assert.Equal(1, report.WinningTrades);
        Assert.Equal(0, report.LosingTrades);
        Assert.Equal(1.75m, report.NetProfitLoss);
        Assert.Equal(10_001.75m, report.EndingEquity);
        Assert.Equal(1d, report.WinRate);
        Assert.Null(report.ProfitFactor);
        var trade = Assert.Single(report.ClosedTrades);
        Assert.Equal(1.75m, trade.NetProfitLoss);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        Assert.Equal(1, json.RootElement.GetProperty("ClosedTrades").GetArrayLength());
        Assert.Equal(
            1.75m,
            json.RootElement.GetProperty("ClosedTrades")[0].GetProperty("NetProfitLoss").GetDecimal());
    }

    [Fact]
    public async Task Replay_Applies_Open_Position_Risk_Limit()
    {
        var mesBars = CreateReplayBars();
        var mnqBars = mesBars.Select(bar => bar with { Symbol = "MNQ" });
        var bars = mesBars.Concat(mnqBars).ToArray();

        var report = await new HistoricalReplayRunner().RunAsync(bars);

        Assert.Equal(2, report.SignalCandidates);
        Assert.Equal(1, report.OrdersPlaced);
        Assert.Equal(1, report.RiskRejected);
        Assert.Equal(1, report.TradesClosed);
    }

    [Fact]
    public async Task Replay_Keeps_Drawdown_HighWatermark_Across_Days()
    {
        var start = new DateTimeOffset(2025, 1, 2, 9, 30, 0, TimeSpan.FromHours(-5));
        var bars = CreateDayBars(start, endWithLoss: true)
            .Concat(CreateDayBars(start.AddDays(1), endWithLoss: false))
            .ToArray();
        var options = new HistoricalReplayOptions(
            InitialEquity: 10_000m,
            FeePerContract: 1.25m,
            SlippageTicks: 1,
            RiskLimits: new RiskLimits(1, 0.10, 0.001));

        var report = await new HistoricalReplayRunner(options).RunAsync(bars);

        Assert.Equal(2, report.SignalCandidates);
        Assert.Equal(1, report.OrdersPlaced);
        Assert.Equal(1, report.RiskRejected);
        Assert.Equal(1, report.TradesClosed);
        Assert.True(report.NetProfitLoss < 0);
    }

    [Fact]
    public async Task Replay_Closes_At_Configured_NewYork_Day_Boundary_For_Utc_Bars()
    {
        var start = new DateTimeOffset(2025, 1, 2, 15, 0, 0, TimeSpan.FromHours(-5));
        var bars = CreateReplayBars()
            .Select((bar, index) => bar with { Timestamp = start.AddMinutes(index * 5) })
            .ToList();
        bars.Add(CreateBar(start, 102, 124.5m));
        bars.Add(CreateBar(start, 103, 124.5m));
        bars.Add(CreateBar(start, 104, 124.5m));
        bars.Add(CreateBar(start, 105, 124.5m));
        bars.Add(CreateBar(start, 106, 124.5m));
        bars.Add(CreateBar(start, 107, 124.5m));
        bars.Add(CreateBar(start, 108, 120m));
        bars.Add(CreateBar(start, 109, 120m));

        var report = await new HistoricalReplayRunner().RunAsync(bars);
        var trade = Assert.Single(report.ClosedTrades);

        Assert.Equal("America/New_York", report.TimeZoneId);
        Assert.Equal(124.25m, trade.ExitPrice);
    }

    [Fact]
    public async Task Replay_Uses_Configured_Timezone_For_Risk_Day_Reset()
    {
        var start = new DateTimeOffset(2025, 1, 2, 15, 0, 0, TimeSpan.FromHours(-5));
        var dayOne = CreateDayBars(start, endWithLoss: true);
        var dayTwo = CreateDayBars(start.AddDays(1), endWithLoss: false);
        var utcDatedBars = dayOne.Concat(dayTwo)
            .Select(bar => bar with { Timestamp = bar.Timestamp.ToUniversalTime() })
            .ToArray();
        var options = new HistoricalReplayOptions(
            InitialEquity: 10_000m,
            FeePerContract: 1.25m,
            SlippageTicks: 1,
            RiskLimits: new RiskLimits(1, 0.10, 0.001),
            TimeZoneId: "America/New_York");

        var report = await new HistoricalReplayRunner(options).RunAsync(utcDatedBars);

        Assert.Equal(2, report.SignalCandidates);
        Assert.Equal(1, report.OrdersPlaced);
        Assert.Equal(1, report.RiskRejected);
        Assert.Equal(1, report.TradesClosed);
    }

    [Fact]
    public async Task Replay_Rejects_Duplicate_Bars()
    {
        var bars = CreateReplayBars().ToList();
        bars.Add(bars[0]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new HistoricalReplayRunner().RunAsync(bars));
    }

    [Fact]
    public async Task Included_Synthetic_Csv_Sample_Exercises_A_Complete_Replay()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "sample_mes_5m_synthetic.csv");
        var bars = await HistoricalBarCsvReader.ReadFileAsync(path);
        var report = await new HistoricalReplayRunner().RunAsync(bars);

        Assert.Equal(102, report.BarsProcessed);
        Assert.Equal(1, report.OrdersPlaced);
        Assert.Equal(1, report.TradesClosed);
        Assert.Equal(1.25m, report.NetProfitLoss);
    }

    [Theory]
    [InlineData("0", "1.25", 1, 1)]
    [InlineData("10000", "-1", 1, 1)]
    [InlineData("10000", "1.25", -1, 1)]
    [InlineData("10000", "1.25", 1, 0)]
    [InlineData("10000", "1.25", 1, 1, 0)]
    public void Constructor_Rejects_Invalid_Options(
        string initialEquity,
        string fee,
        int slippageTicks,
        int quantity,
        int maxPositions = 1)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoricalReplayRunner(new HistoricalReplayOptions(
                InitialEquity: decimal.Parse(initialEquity, CultureInfo.InvariantCulture),
                FeePerContract: decimal.Parse(fee, CultureInfo.InvariantCulture),
                SlippageTicks: slippageTicks,
                OrderQuantity: quantity,
                MaxOpenPositions: maxPositions)));
    }
}
