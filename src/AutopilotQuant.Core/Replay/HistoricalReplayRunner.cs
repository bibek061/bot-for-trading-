using AutopilotQuant.Core.Broker;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Risk;
using AutopilotQuant.Core.Signals;

namespace AutopilotQuant.Core.Replay;

public sealed record HistoricalReplayOptions(
    decimal InitialEquity = 10_000m,
    decimal FeePerContract = 1.25m,
    int SlippageTicks = 1,
    int OrderQuantity = 1,
    int MaxOpenPositions = 1,
    RiskLimits? RiskLimits = null,
    bool AllowShorts = false,
    string TimeZoneId = "America/New_York");

public sealed record HistoricalReplayReport(
    int BarsProcessed,
    int SignalCandidates,
    int OrdersPlaced,
    int RiskRejected,
    int OtherOrderRejections,
    int TradesClosed,
    int WinningTrades,
    int LosingTrades,
    decimal GrossProfit,
    decimal GrossLoss,
    decimal NetProfitLoss,
    decimal EndingEquity,
    double WinRate,
    double? ProfitFactor,
    IReadOnlyList<ClosedPaperTrade> ClosedTrades,
    string TimeZoneId);

public sealed class HistoricalReplayRunner
{
    private readonly HistoricalReplayOptions _options;

    public HistoricalReplayRunner(HistoricalReplayOptions? options = null)
    {
        _options = options ?? new HistoricalReplayOptions();
        if (_options.InitialEquity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Initial equity must be positive.");
        if (_options.FeePerContract < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Fee per contract cannot be negative.");
        if (_options.SlippageTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Slippage ticks cannot be negative.");
        if (_options.OrderQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Order quantity must be positive.");
        if (_options.MaxOpenPositions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum open positions must be positive.");
        if (string.IsNullOrWhiteSpace(_options.TimeZoneId))
            throw new ArgumentException("Replay timezone ID is required.", nameof(options));
        _ = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId);
    }

    public async Task<HistoricalReplayReport> RunAsync(
        IReadOnlyList<MarketBar> inputBars,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputBars);
        if (inputBars.Count == 0)
            throw new ArgumentException("Replay requires at least one market bar.", nameof(inputBars));

        var bars = inputBars
            .OrderBy(bar => bar.Timestamp)
            .ThenBy(bar => bar.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ValidateUniqueTimestamps(bars);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId);

        var broker = new PaperBroker(new PaperBrokerOptions(
            _options.InitialEquity,
            _options.FeePerContract,
            _options.SlippageTicks,
            _options.AllowShorts));
        var coordinator = new PaperSignalCoordinator(
            broker,
            new PaperSignalCoordinatorOptions(
                _options.OrderQuantity,
                _options.MaxOpenPositions,
                _options.RiskLimits,
                _options.TimeZoneId));
        var histories = new Dictionary<string, List<MarketBar>>(StringComparer.OrdinalIgnoreCase);
        var latestBars = new Dictionary<string, MarketBar>(StringComparer.OrdinalIgnoreCase);
        var currentDay = GetTradingDay(bars[0].Timestamp, timeZone);
        var signalCandidates = 0;
        var ordersPlaced = 0;
        var riskRejected = 0;
        var otherOrderRejections = 0;

        foreach (var bar in bars)
        {
            ct.ThrowIfCancellationRequested();
            var barDay = GetTradingDay(bar.Timestamp, timeZone);
            if (barDay != currentDay)
            {
                await CloseAllPositionsAsync(broker, latestBars, ct);
                currentDay = barDay;
            }

            if (!histories.TryGetValue(bar.Symbol, out var history))
            {
                history = [];
                histories.Add(bar.Symbol, history);
            }
            history.Add(bar);
            latestBars[bar.Symbol] = bar;

            var result = await coordinator.ProcessCompletedBarsAsync(history, ct);
            if (result.Evaluation.Signal is not null)
                signalCandidates++;
            if (result.Status == PaperSignalProcessingStatus.OrderPlaced)
                ordersPlaced++;
            else if (result.Status == PaperSignalProcessingStatus.RiskRejected
                     || result.Status == PaperSignalProcessingStatus.PositionLimitReached)
                riskRejected++;
            else if (result.Status == PaperSignalProcessingStatus.OrderRejected)
                otherOrderRejections++;
        }

        await CloseAllPositionsAsync(broker, latestBars, ct);
        var closedTrades = broker.ClosedTrades;
        var winners = closedTrades.Count(trade => trade.NetProfitLoss > 0);
        var losers = closedTrades.Count(trade => trade.NetProfitLoss < 0);
        var grossProfit = closedTrades
            .Where(trade => trade.GrossProfitLoss > 0)
            .Sum(trade => trade.GrossProfitLoss);
        var grossLoss = closedTrades
            .Where(trade => trade.GrossProfitLoss < 0)
            .Sum(trade => Math.Abs(trade.GrossProfitLoss));
        var netWinningTrades = closedTrades
            .Where(trade => trade.NetProfitLoss > 0)
            .Sum(trade => trade.NetProfitLoss);
        var netLosingTrades = closedTrades
            .Where(trade => trade.NetProfitLoss < 0)
            .Sum(trade => Math.Abs(trade.NetProfitLoss));
        var winRate = closedTrades.Count == 0 ? 0 : (double)winners / closedTrades.Count;
        double? profitFactor = netLosingTrades == 0
            ? null
            : (double)(netWinningTrades / netLosingTrades);

        return new HistoricalReplayReport(
            bars.Length,
            signalCandidates,
            ordersPlaced,
            riskRejected,
            otherOrderRejections,
            closedTrades.Count,
            winners,
            losers,
            grossProfit,
            grossLoss,
            broker.RealizedNetProfitLoss,
            await broker.GetAccountEquityAsync(ct),
            winRate,
            profitFactor,
            closedTrades,
            _options.TimeZoneId);
    }

    private static async Task CloseAllPositionsAsync(
        PaperBroker broker,
        IReadOnlyDictionary<string, MarketBar> latestBars,
        CancellationToken ct)
    {
        foreach (var position in broker.OpenPositions)
        {
            if (!latestBars.TryGetValue(position.Symbol, out var lastBar))
                throw new InvalidOperationException($"No final replay price is available for {position.Symbol}.");
            var close = await broker.ClosePositionAsync(position.PositionId, lastBar.Close, ct);
            if (!close.Ok)
                throw new InvalidOperationException($"Unable to close paper position at replay boundary: {close.Error}");
        }
    }

    private static void ValidateUniqueTimestamps(IReadOnlyList<MarketBar> bars)
    {
        var seen = new HashSet<(string Symbol, DateTimeOffset Timestamp)>();
        foreach (var bar in bars)
        {
            if (!seen.Add((bar.Symbol.ToUpperInvariant(), bar.Timestamp)))
                throw new ArgumentException(
                    $"Replay contains duplicate bar for {bar.Symbol} at {bar.Timestamp:O}.",
                    nameof(bars));
        }
    }

    private static DateOnly GetTradingDay(DateTimeOffset timestamp, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime);
}
