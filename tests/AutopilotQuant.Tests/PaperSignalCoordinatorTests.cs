using AutopilotQuant.Core.Broker;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Signals;
using Xunit;

namespace AutopilotQuant.Tests;

public class PaperSignalCoordinatorTests
{
    private static IReadOnlyList<MarketBar> CreateSignalBars(string symbol, int minuteOffset = 0)
    {
        var bars = new List<MarketBar>();
        for (var i = 0; i < 98; i++)
            bars.Add(CreateBar(symbol, i, 100m + i * 0.25m, minuteOffset));

        bars.Add(CreateBar(symbol, 98, 122.25m, minuteOffset));
        bars.Add(CreateBar(symbol, 99, 120.25m, minuteOffset));
        bars.Add(CreateBar(symbol, 100, 123.25m, minuteOffset));
        return bars;
    }

    private static MarketBar CreateBar(string symbol, int index, decimal close, int minuteOffset) =>
        new(
            symbol,
            new DateTimeOffset(2025, 1, 2, 9, 30, 0, TimeSpan.FromHours(-5))
                .AddMinutes(minuteOffset + index * 5),
            close,
            close + 0.25m,
            close - 0.25m,
            close,
            100);

    private static PaperBroker CreateBroker() =>
        new(new PaperBrokerOptions(10_000m, 1m, 1));

    [Fact]
    public async Task ProcessCompletedBars_Places_One_Paper_Order_For_Confirmed_Signal()
    {
        var broker = CreateBroker();
        var coordinator = new PaperSignalCoordinator(broker);
        var bars = CreateSignalBars("MES");

        var result = await coordinator.ProcessCompletedBarsAsync(bars, CancellationToken.None);

        Assert.Equal(PaperSignalProcessingStatus.OrderPlaced, result.Status);
        Assert.True(result.Order!.Ok);
        Assert.True(result.RiskDecision!.Approved);
        var position = Assert.Single(broker.OpenPositions);
        Assert.Equal(result.Order.PositionId, position.PositionId);
        Assert.Equal("MES", position.Symbol);
        Assert.Equal("BUY", position.Side);
        Assert.Equal(1, position.Quantity);
        Assert.Equal(bars[^1].Close + 0.25m, position.EntryPrice);
        Assert.Single(broker.Fills);
    }

    [Fact]
    public async Task Reprocessing_Same_Bar_Does_Not_Create_Duplicate_Order()
    {
        var broker = CreateBroker();
        var coordinator = new PaperSignalCoordinator(broker);
        var bars = CreateSignalBars("MES");

        var first = await coordinator.ProcessCompletedBarsAsync(bars, CancellationToken.None);
        var second = await coordinator.ProcessCompletedBarsAsync(bars, CancellationToken.None);

        Assert.Equal(PaperSignalProcessingStatus.OrderPlaced, first.Status);
        Assert.Equal(PaperSignalProcessingStatus.DuplicateBar, second.Status);
        Assert.Single(broker.Fills);
        Assert.Single(broker.OpenPositions);
    }

    [Fact]
    public async Task OutOfOrder_Bars_Are_Not_Executed()
    {
        var broker = CreateBroker();
        var coordinator = new PaperSignalCoordinator(broker);
        var latest = CreateSignalBars("MES");
        var older = CreateSignalBars("MES", minuteOffset: -5);

        var first = await coordinator.ProcessCompletedBarsAsync(latest, CancellationToken.None);
        var second = await coordinator.ProcessCompletedBarsAsync(older, CancellationToken.None);

        Assert.Equal(PaperSignalProcessingStatus.OrderPlaced, first.Status);
        Assert.Equal(PaperSignalProcessingStatus.OutOfOrderBar, second.Status);
        Assert.Single(broker.Fills);
    }

    [Fact]
    public async Task Position_Limit_Blocks_Signal_For_Another_Instrument()
    {
        var broker = CreateBroker();
        var coordinator = new PaperSignalCoordinator(broker);

        var first = await coordinator.ProcessCompletedBarsAsync(
            CreateSignalBars("MES"),
            CancellationToken.None);
        var second = await coordinator.ProcessCompletedBarsAsync(
            CreateSignalBars("MNQ"),
            CancellationToken.None);

        Assert.Equal(PaperSignalProcessingStatus.OrderPlaced, first.Status);
        Assert.Equal(PaperSignalProcessingStatus.PositionLimitReached, second.Status);
        Assert.Single(broker.OpenPositions);
        Assert.Single(broker.Fills);
    }

    [Fact]
    public async Task New_Bar_Updates_Mark_And_NoSignal_Does_Not_Create_Order()
    {
        var broker = CreateBroker();
        var coordinator = new PaperSignalCoordinator(broker);
        var bars = CreateSignalBars("MES").ToList();
        var entry = await coordinator.ProcessCompletedBarsAsync(bars, CancellationToken.None);
        Assert.Equal(PaperSignalProcessingStatus.OrderPlaced, entry.Status);

        bars.Add(CreateBar("MES", 101, 124.5m, 0));
        var next = await coordinator.ProcessCompletedBarsAsync(bars, CancellationToken.None);

        Assert.Equal(PaperSignalProcessingStatus.NoSignal, next.Status);
        Assert.Equal(5m, broker.UnrealizedGrossProfitLoss);
        Assert.Single(broker.Fills);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Constructor_Rejects_Invalid_Options(int quantity, int maxOpenPositions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PaperSignalCoordinator(
                CreateBroker(),
                new PaperSignalCoordinatorOptions(quantity, maxOpenPositions)));
    }

    [Fact]
    public async Task Processor_Honors_Cancellation()
    {
        var coordinator = new PaperSignalCoordinator(CreateBroker());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ProcessCompletedBarsAsync(CreateSignalBars("MES"), cancellation.Token));
    }

    [Fact]
    public async Task Risk_Engine_Daily_Loss_Limit_Blocks_Paper_Signal()
    {
        var broker = CreateBroker();
        var coordinator = new PaperSignalCoordinator(
            broker,
            new PaperSignalCoordinatorOptions(
                RiskLimits: new AutopilotQuant.Core.Risk.RiskLimits(1, 0.001, 0.10)));
        var openingBars = CreateSignalBars("MES").Take(50).ToArray();
        var baseline = await coordinator.ProcessCompletedBarsAsync(openingBars, CancellationToken.None);
        Assert.Equal(PaperSignalProcessingStatus.NoSignal, baseline.Status);

        var losingOrder = await broker.PlaceOrderAsync(
            new OrderRequest("MES", "BUY", 1, "MARKET", null, "seed-loss", 5000m),
            CancellationToken.None);
        var position = Assert.Single(broker.OpenPositions);
        var losingClose = await broker.ClosePositionAsync(position.PositionId, 4990m, CancellationToken.None);
        Assert.True(losingOrder.Ok);
        Assert.True(losingClose.Ok);

        var result = await coordinator.ProcessCompletedBarsAsync(
            CreateSignalBars("MES"),
            CancellationToken.None);

        Assert.Equal(PaperSignalProcessingStatus.RiskRejected, result.Status);
        Assert.False(result.RiskDecision!.Approved);
        Assert.Contains("Daily loss limit", result.Reason);
        Assert.Empty(broker.OpenPositions);
    }
}
