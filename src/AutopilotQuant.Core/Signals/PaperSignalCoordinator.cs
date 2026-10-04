using AutopilotQuant.Core.Broker;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Risk;

namespace AutopilotQuant.Core.Signals;

public sealed record PaperSignalCoordinatorOptions(
    int OrderQuantity = 1,
    int MaxOpenPositions = 1,
    RiskLimits? RiskLimits = null,
    string TimeZoneId = "America/New_York");

public enum PaperSignalProcessingStatus
{
    DuplicateBar,
    OutOfOrderBar,
    NoSignal,
    PositionLimitReached,
    RiskRejected,
    OrderRejected,
    OrderPlaced
}

public sealed record PaperSignalProcessingResult(
    SignalEvaluation Evaluation,
    PaperSignalProcessingStatus Status,
    OrderResult? Order,
    string Reason,
    RiskDecision? RiskDecision = null);

public sealed class PaperSignalCoordinator
{
    private readonly PaperBroker _broker;
    private readonly LongPullbackSignalEngine _signalEngine;
    private readonly PaperSignalCoordinatorOptions _options;
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastProcessedBar =
        new(StringComparer.OrdinalIgnoreCase);
    private DateOnly? _currentTradingDay;
    private decimal _dayStartEquity;
    private decimal _peakEquity;
    private readonly RiskLimits _riskLimits;
    private readonly TimeZoneInfo _timeZone;

    public PaperSignalCoordinator(
        PaperBroker broker,
        PaperSignalCoordinatorOptions? options = null,
        LongPullbackSignalEngine? signalEngine = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
        _options = options ?? new PaperSignalCoordinatorOptions();
        if (_options.OrderQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Paper order quantity must be positive.");
        if (_options.MaxOpenPositions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum open paper positions must be positive.");
        _riskLimits = _options.RiskLimits ?? RiskLimits.Default with
        {
            MaxOpenPositions = _options.MaxOpenPositions
        };
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId);
        _signalEngine = signalEngine ?? new LongPullbackSignalEngine();
    }

    public async Task<PaperSignalProcessingResult> ProcessCompletedBarsAsync(
        IReadOnlyList<MarketBar> bars,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (bars.Count == 0)
            throw new ArgumentException("At least one completed bar is required.", nameof(bars));

        await _processingLock.WaitAsync(ct);
        try
        {
            var evaluation = _signalEngine.Evaluate(bars);
            var lastBar = bars[^1];
            if (_lastProcessedBar.TryGetValue(lastBar.Symbol, out var lastProcessed))
            {
                if (lastBar.Timestamp == lastProcessed)
                    return new PaperSignalProcessingResult(
                        evaluation,
                        PaperSignalProcessingStatus.DuplicateBar,
                        null,
                        "Completed bar was already processed.");
                if (lastBar.Timestamp < lastProcessed)
                    return new PaperSignalProcessingResult(
                        evaluation,
                        PaperSignalProcessingStatus.OutOfOrderBar,
                        null,
                        "Completed bar is older than the latest processed bar.");
            }

            _lastProcessedBar[lastBar.Symbol] = lastBar.Timestamp;
            var tradingDay = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(lastBar.Timestamp, _timeZone).DateTime);
            var equity = await _broker.GetAccountEquityAsync(ct);
            if (_currentTradingDay != tradingDay)
            {
                _currentTradingDay = tradingDay;
                _dayStartEquity = equity;
            }
            if (equity > _peakEquity)
            {
                _peakEquity = equity;
            }

            if (_broker.OpenPositions.Any(position =>
                    string.Equals(position.Symbol, lastBar.Symbol, StringComparison.OrdinalIgnoreCase)))
                _broker.MarkToMarket(lastBar.Symbol, lastBar.Close);

            if (evaluation.Signal is not EntrySignal signal)
                return new PaperSignalProcessingResult(
                    evaluation,
                    PaperSignalProcessingStatus.NoSignal,
                    null,
                    evaluation.Reason);

            var connected = await _broker.IsConnectedAsync(ct);
            var currentEquity = await _broker.GetAccountEquityAsync(ct);
            if (currentEquity > _peakEquity)
                _peakEquity = currentEquity;
            var riskState = new RiskState(
                _broker.OpenPositions.Count,
                CalculateLossPct(_dayStartEquity, currentEquity),
                CalculateLossPct(_peakEquity, currentEquity),
                true,
                connected,
                false,
                PaperTrading: true);
            var riskDecision = RiskEngine.Evaluate(riskState, _riskLimits);
            if (!riskDecision.Approved)
            {
                var status = riskDecision.Reason == "Maximum open positions reached."
                    ? PaperSignalProcessingStatus.PositionLimitReached
                    : PaperSignalProcessingStatus.RiskRejected;
                return new PaperSignalProcessingResult(
                    evaluation,
                    status,
                    null,
                    riskDecision.Reason,
                    riskDecision);
            }

            var order = await _broker.PlaceOrderAsync(
                new OrderRequest(
                    signal.Symbol,
                    signal.Side,
                    _options.OrderQuantity,
                    "MARKET",
                    null,
                    signal.Tag,
                    signal.ReferencePrice),
                ct);
            return new PaperSignalProcessingResult(
                evaluation,
                order.Ok ? PaperSignalProcessingStatus.OrderPlaced : PaperSignalProcessingStatus.OrderRejected,
                order,
                order.Ok ? "Paper order placed." : order.Error ?? "Paper order was rejected.",
                riskDecision);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private static double CalculateLossPct(decimal baseline, decimal current)
    {
        if (baseline <= 0 || current >= baseline)
            return 0;
        return (double)((baseline - current) / baseline);
    }
}
