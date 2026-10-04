using AutopilotQuant.Core.Broker;
using AutopilotQuant.Core.MarketData;

namespace AutopilotQuant.Core.Forward;

public sealed record ForwardSettings(
    decimal InitialEquity = 10_000m,
    decimal FeePerContract = 1.25m,
    int SlippageTicks = 1,
    int Quantity = 1,
    int MaxOpenPositions = 1,
    decimal DailyLossLimitPct = 0.03m,
    decimal MaxDrawdownPct = 0.10m,
    decimal RiskPerTradePct = 0.01m,
    int StopTicks = 20,
    int TargetTicks = 40,
    int MaxSpreadTicks = 4,
    int QuoteMaxAgeSeconds = 15,
    string TimeZoneId = "America/New_York",
    int SessionResetHour = 18,
    string EntryStart = "09:35",
    string EntryEnd = "15:45");

// Timestamps are UTC instants. Bar.Timestamp is the END of a completed five-minute bar.
// ContractId is the provider's actual expiring contract, never a continuous-series alias.
public sealed record FeedQuote(string Symbol, string ContractId, DateTimeOffset Timestamp,
    decimal Bid, decimal Ask);
public sealed record FeedBar(string ContractId, MarketBar Bar, bool Warmup = false);
public sealed record PendingEntry(string Symbol, string ContractId, DateTimeOffset SignalAt,
    DateTimeOffset ReceivedAt);
public sealed record PaperProtection(string PositionId, decimal StopPrice, decimal TargetPrice);
public sealed record SessionEvent(DateTimeOffset At, string Kind, string Message);
public sealed record InstrumentView(string Symbol, string? ContractId, FeedQuote? Quote,
    bool Fresh, IReadOnlyList<MarketBar> Bars, string SignalReason, decimal? FastEma, decimal? SlowEma);
public sealed record SessionView(string Mode, bool Paused, string PauseReason, bool RiskHalted,
    decimal Equity, decimal RealizedNet, decimal UnrealizedGross, decimal Fees,
    decimal DailyLossPct, decimal DrawdownPct, ForwardSettings Settings,
    IReadOnlyList<PaperPosition> Positions, IReadOnlyList<PaperProtection> Protection,
    IReadOnlyList<ClosedPaperTrade> Trades, IReadOnlyList<PaperFill> Fills,
    IReadOnlyList<PendingEntry> Pending, IReadOnlyList<InstrumentView> Instruments,
    IReadOnlyList<SessionEvent> Events, DateTimeOffset ServerTime);

public sealed class PaperSessionState
{
    public int Version { get; set; } = 1;
    public ForwardSettings Settings { get; set; } = new();
    public PaperBrokerState Broker { get; set; } = new([], [], [], new());
    public bool Paused { get; set; } = true;
    public string PauseReason { get; set; } = "Waiting for a configured data adapter.";
    public bool RiskHalted { get; set; }
    public bool FlattenRequested { get; set; }
    public DateOnly? SessionDate { get; set; }
    public decimal DayStartEquity { get; set; } = 10_000m;
    public decimal PeakEquity { get; set; } = 10_000m;
    public Dictionary<string, string> Contracts { get; set; } = new();
    public Dictionary<string, FeedQuote> Quotes { get; set; } = new();
    public Dictionary<string, List<MarketBar>> Histories { get; set; } = new();
    public Dictionary<string, PendingEntry> Pending { get; set; } = new();
    public Dictionary<string, PaperProtection> Protection { get; set; } = new();
    public List<SessionEvent> Events { get; set; } = [];
}
