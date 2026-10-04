using System.Globalization;
using AutopilotQuant.Core.Broker;
using Xunit;

namespace AutopilotQuant.Tests;

public class PaperBrokerTests
{
    private static PaperBroker CreateBroker(bool allowShorts = false) =>
        new(new PaperBrokerOptions(10_000m, 1.25m, 1, allowShorts));

    [Theory]
    [InlineData("BUY", "5000.00", "5000.25")]
    [InlineData("SELL", "5000.00", "4999.75")]
    public async Task Market_Order_Records_Adverse_Slippage_And_Fee(
        string side,
        string referencePrice,
        string expectedFillPrice)
    {
        var broker = CreateBroker(side == "SELL");
        var parsedReferencePrice = decimal.Parse(referencePrice, CultureInfo.InvariantCulture);
        var request = new OrderRequest(
            "MES",
            side,
            2,
            "MARKET",
            null,
            "test",
            parsedReferencePrice);

        var result = await broker.PlaceOrderAsync(request, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(result.OrderId);
        var fill = Assert.Single(broker.Fills);
        Assert.Equal(result.OrderId, fill.OrderId);
        Assert.Equal(decimal.Parse(expectedFillPrice, CultureInfo.InvariantCulture), fill.FillPrice);
        Assert.Equal(2.50m, fill.Fee);
        Assert.Equal(2.50m, broker.TotalFees);
        Assert.Equal(9_995m, await broker.GetAccountEquityAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("ES", "BUY", 1, "MARKET", null, "5000.00", "Unsupported paper-trading symbol.")]
    [InlineData("MES", "HOLD", 1, "MARKET", null, "5000.00", "Order side must be BUY or SELL.")]
    [InlineData("MES", "BUY", 0, "MARKET", null, "5000.00", "Order quantity must be positive.")]
    [InlineData("MES", "BUY", 1, "LIMIT", "5000.00", "5000.00", "Paper broker supports market orders only.")]
    [InlineData("MES", "BUY", 1, "MARKET", null, null, "A positive reference price is required for paper fills.")]
    [InlineData("MES", "BUY", 1, "MARKET", null, "0", "A positive reference price is required for paper fills.")]
    [InlineData("MES", "BUY", 1, "MARKET", null, "5000.10", "Reference price must align to the contract tick size.")]
    public async Task Invalid_Orders_Are_Rejected_Without_A_Fill(
        string symbol,
        string side,
        int quantity,
        string orderType,
        string? limitPrice,
        string? referencePrice,
        string expectedError)
    {
        var broker = CreateBroker();
        decimal? parsedLimitPrice = limitPrice is null
            ? null
            : decimal.Parse(limitPrice, CultureInfo.InvariantCulture);
        decimal? parsedReferencePrice = referencePrice is null
            ? null
            : decimal.Parse(referencePrice, CultureInfo.InvariantCulture);
        var request = new OrderRequest(
            symbol,
            side,
            quantity,
            orderType,
            parsedLimitPrice,
            "test",
            parsedReferencePrice);

        var result = await broker.PlaceOrderAsync(request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(expectedError, result.Error);
        Assert.Empty(broker.Fills);
        Assert.Equal(10_000m, await broker.GetAccountEquityAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Broker_Rejects_Fees_That_Exceed_Simulated_Equity()
    {
        var broker = new PaperBroker(new PaperBrokerOptions(1m, 1.25m, 0));
        var request = new OrderRequest("MNQ", "BUY", 1, "MARKET", null, "test", 20_000m);

        var result = await broker.PlaceOrderAsync(request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Insufficient simulated equity to cover fees.", result.Error);
        Assert.Empty(broker.Fills);
        Assert.Equal(1m, await broker.GetAccountEquityAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Closed_Trade_Tracks_Fees_Realized_Pnl_And_Equity()
    {
        var broker = CreateBroker();
        var entry = await broker.PlaceOrderAsync(
            new OrderRequest("MES", "BUY", 2, "MARKET", null, "test", 5000m),
            CancellationToken.None);
        var position = Assert.Single(broker.OpenPositions);
        Assert.Equal(entry.PositionId, position.PositionId);
        Assert.Equal(5000.25m, position.EntryPrice);

        broker.MarkToMarket("MES", 5001m);
        Assert.Equal(7.50m, broker.UnrealizedGrossProfitLoss);
        Assert.Equal(10_005m, await broker.GetAccountEquityAsync(CancellationToken.None));

        var close = await broker.ClosePositionAsync(position.PositionId, 5001m, CancellationToken.None);

        Assert.True(close.Ok);
        Assert.NotNull(close.Trade);
        Assert.Equal(5000.75m, close.Trade.ExitPrice);
        Assert.Equal(5m, close.Trade.GrossProfitLoss);
        Assert.Equal(5m, close.Trade.EntryFee + close.Trade.ExitFee);
        Assert.Equal(0m, close.Trade.NetProfitLoss);
        Assert.Empty(broker.OpenPositions);
        Assert.Single(broker.ClosedTrades);
        Assert.Equal(5m, broker.RealizedGrossProfitLoss);
        Assert.Equal(0m, broker.RealizedNetProfitLoss);
        Assert.Equal(0m, broker.UnrealizedGrossProfitLoss);
        Assert.Equal(5m, broker.TotalFees);
        Assert.Equal(10_000m, await broker.GetAccountEquityAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Independent_Orders_Create_Separate_Positions_And_Close_Individually()
    {
        var broker = CreateBroker();
        var first = await broker.PlaceOrderAsync(
            new OrderRequest("MES", "BUY", 1, "MARKET", null, "first", 5000m),
            CancellationToken.None);
        var second = await broker.PlaceOrderAsync(
            new OrderRequest("MES", "BUY", 1, "MARKET", null, "second", 5000.25m),
            CancellationToken.None);
        var positions = broker.OpenPositions;

        Assert.Equal(2, positions.Count);
        Assert.NotEqual(first.PositionId, second.PositionId);
        Assert.NotEqual(positions[0].PositionId, positions[1].PositionId);

        var close = await broker.ClosePositionAsync(positions[0].PositionId, 5000.5m, CancellationToken.None);

        Assert.True(close.Ok);
        Assert.Single(broker.OpenPositions);
        Assert.Equal(positions[1].PositionId, Assert.Single(broker.OpenPositions).PositionId);
        Assert.Single(broker.ClosedTrades);
        Assert.Equal(3, broker.Fills.Count);
    }

    [Fact]
    public async Task Short_Position_Uses_Inverse_Price_Movement_For_Pnl()
    {
        var broker = CreateBroker(allowShorts: true);
        var entry = await broker.PlaceOrderAsync(
            new OrderRequest("MNQ", "SELL", 1, "MARKET", null, "short", 20_000m),
            CancellationToken.None);
        var position = Assert.Single(broker.OpenPositions);

        broker.MarkToMarket("MNQ", 19_998.75m);
        Assert.Equal(2m, broker.UnrealizedGrossProfitLoss);

        var close = await broker.ClosePositionAsync(position.PositionId, 19_999m, CancellationToken.None);

        Assert.True(close.Ok);
        Assert.Equal(1m, close.Trade!.GrossProfitLoss);
        Assert.Equal(-1.50m, close.Trade.NetProfitLoss);
        Assert.Equal(entry.PositionId, close.Trade.PositionId);
    }

    [Fact]
    public async Task Short_Entry_Is_Rejected_By_Default()
    {
        var broker = CreateBroker();

        var result = await broker.PlaceOrderAsync(
            new OrderRequest("MES", "SELL", 1, "MARKET", null, "short", 5000m),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Short exposure is locked.", result.Error);
        Assert.Empty(broker.OpenPositions);
        Assert.Empty(broker.Fills);
    }

    [Fact]
    public async Task Losing_Position_Can_Still_Be_Closed_When_Equity_Cannot_Cover_Exit_Fee()
    {
        var broker = new PaperBroker(new PaperBrokerOptions(2.5m, 1.25m, 1));
        var entry = await broker.PlaceOrderAsync(
            new OrderRequest("MES", "BUY", 1, "MARKET", null, "test", 5000.25m),
            CancellationToken.None);
        Assert.True(entry.Ok);
        var position = Assert.Single(broker.OpenPositions);
        broker.MarkToMarket("MES", 4999.75m);
        Assert.True(await broker.GetAccountEquityAsync(CancellationToken.None) < 0);

        var close = await broker.ClosePositionAsync(position.PositionId, 4999.75m, CancellationToken.None);

        Assert.True(close.Ok);
        Assert.Empty(broker.OpenPositions);
        Assert.Equal(-5m, close.Trade!.GrossProfitLoss);
        Assert.Equal(-5m, await broker.GetAccountEquityAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_Close_Leaves_Position_Open_And_Closed_Close_Is_Rejected()
    {
        var broker = CreateBroker();
        var entry = await broker.PlaceOrderAsync(
            new OrderRequest("MES", "BUY", 1, "MARKET", null, "test", 5000m),
            CancellationToken.None);
        var position = Assert.Single(broker.OpenPositions);

        var invalidClose = await broker.ClosePositionAsync(position.PositionId, 5000.1m, CancellationToken.None);
        Assert.False(invalidClose.Ok);
        Assert.Equal("Reference price must align to the contract tick size.", invalidClose.Error);
        Assert.Single(broker.OpenPositions);

        var validClose = await broker.ClosePositionAsync(position.PositionId, 5000.25m, CancellationToken.None);
        Assert.True(validClose.Ok);
        var repeatedClose = await broker.ClosePositionAsync(position.PositionId, 5000.25m, CancellationToken.None);

        Assert.False(repeatedClose.Ok);
        Assert.Equal("Paper position was not found or is already closed.", repeatedClose.Error);
        Assert.Empty(broker.OpenPositions);
        Assert.Single(broker.ClosedTrades);
        Assert.Equal(2, broker.Fills.Count);
        Assert.NotNull(entry.PositionId);
    }

    [Fact]
    public void MarkToMarket_Rejects_Unknown_Symbols_And_Invalid_Prices()
    {
        var broker = CreateBroker();

        Assert.Throws<ArgumentException>(() => broker.MarkToMarket("ES", 5000m));
        Assert.Throws<ArgumentOutOfRangeException>(() => broker.MarkToMarket("MES", 5000.1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => broker.MarkToMarket("MES", 0m));
    }

    [Fact]
    public async Task Broker_Reports_Paper_Connection_And_Honors_Cancellation()
    {
        var broker = CreateBroker();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => broker.ConnectAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => broker.IsConnectedAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => broker.GetAccountEquityAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => broker.PlaceOrderAsync(
                new OrderRequest("MES", "BUY", 1, "MARKET", null, "test", 5000m),
                cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => broker.ClosePositionAsync("position", 5000m, cancellation.Token));

        Assert.True(await broker.IsConnectedAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("0", "1.25", 1)]
    [InlineData("10000", "-0.01", 1)]
    [InlineData("10000", "1.25", -1)]
    public void Broker_Rejects_Invalid_Configuration(
        string initialEquity,
        string feePerContract,
        int slippageTicks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PaperBroker(new PaperBrokerOptions(
                decimal.Parse(initialEquity, CultureInfo.InvariantCulture),
                decimal.Parse(feePerContract, CultureInfo.InvariantCulture),
                slippageTicks)));
    }
}
