using System.Globalization;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.T4.Protocol;

namespace AutopilotQuant.T4;

public sealed class T4FeedException(string message, bool retryable = true) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

// One instance per expiring market per connection. Snapshot ticker entries are never aggregated.
public sealed class T4MarketAccumulator(T4Market market, DateTimeOffset connectedAt)
{
    private decimal? _bid, _ask;
    private DateTimeOffset? _lastDepth, _lastQuote, _lastTrade;
    private int? _tradeCount;
    private bool _snapshot;
    private DateTimeOffset _completeFrom = Ceiling(connectedAt);
    public MarketBar? FormingBar { get; private set; }
    public bool DefinitionVerified { get; private set; }
    public string Symbol => market.Symbol;

    public void Verify(MarketDetails definition, DateTimeOffset now)
    {
        if (definition.MarketId != market.MarketId || definition.ExchangeId != market.ExchangeId
            || definition.ContractId != market.ProductId || definition.ContractType != 5 || definition.StrategyType != 0
            || definition.Disabled || Number(definition.MinPriceIncrement) != .25m
            || Number(definition.PointValue) != (market.Symbol == "MES" ? 5m : 2m)
            || definition.LastTradingDate is null || definition.LastTradingDate.ToDateTimeOffset() <= now)
            throw new T4FeedException("Contract definition or expiry does not match the selected MES/MNQ instrument.", false);
        DefinitionVerified = true;
    }

    public void Reset(DateTimeOffset now)
    {
        _bid = _ask = null; _lastDepth = _lastQuote = _lastTrade = null;
        _tradeCount = null; _snapshot = false; FormingBar = null; _completeFrom = Ceiling(now);
    }

    public void BeginSnapshot(DateTimeOffset now) { Reset(now); _snapshot = true; }

    public FeedQuote? Depth(MarketDepth depth, DateTimeOffset now, int maxAge)
    {
        if (!DefinitionVerified || depth.MarketId != market.MarketId || !_snapshot) return null;
        if (depth.Delayed) throw new T4FeedException("Delayed market data cannot drive the paper strategy.", false);
        if (depth.Mode != 2 || (depth.Flags & (32 | 64 | 2048)) != 0) return null;
        var at = depth.Time?.ToDateTimeOffset() ?? throw new T4FeedException("Market depth has no event timestamp.");
        if (_lastDepth is { } previous && at < previous) throw new T4FeedException("Out-of-order market depth; a new snapshot is required.");
        _lastDepth = at;
        if (now - at > TimeSpan.FromSeconds(maxAge) || at > now.AddSeconds(2))
            throw new T4FeedException("Market-depth timestamp is stale or in the future.");
        // With TOP_OF_BOOK, a present side replaces that side; an absent side is unchanged.
        // The source timestamp belongs to the reconstructed book after this provider update.
        if (depth.Bids.Count > 0) _bid = Side(depth.Bids, true);
        if (depth.Offers.Count > 0) _ask = Side(depth.Offers, false);
        if (_bid is null || _ask is null) return null;
        if (_ask < _bid) throw new T4FeedException("Crossed market depth; a new snapshot is required.");
        if (_lastQuote is { } last && at <= last) return null;
        _lastQuote = at;
        return new(market.Symbol, market.MarketId, at, _bid.Value, _ask.Value);
    }

    public MarketBar? Trade(MarketTrade trade, DateTimeOffset now, int maxAge)
    {
        if (!DefinitionVerified || !_snapshot || trade.MarketId != market.MarketId) return null;
        if (trade.Delayed) throw new T4FeedException("Delayed trades cannot drive the paper strategy.", false);
        if (trade.Mode != 2) return null;
        var at = trade.Time?.ToDateTimeOffset() ?? throw new T4FeedException("Trade has no event timestamp.");
        if (at > now.AddSeconds(2) || now - at > TimeSpan.FromSeconds(maxAge)) throw new T4FeedException("Trade timestamp is stale or in the future.");
        if (_lastTrade is { } last && at < last) throw new T4FeedException("Out-of-order trades; reconnect and backfill required.");
        if (trade.TotalTradeCount <= 0 || trade.LastTradeVolume <= 0) throw new T4FeedException("Trade counters or volume are invalid.");
        if (_tradeCount == trade.TotalTradeCount) return null;
        if (_tradeCount is { } count && trade.TotalTradeCount != count + 1)
            throw new T4FeedException("Trade counter gap or reset; reconnect and backfill required.");
        _tradeCount = trade.TotalTradeCount; _lastTrade = at;
        var price = TickPrice(trade.LastTradePrice);
        var start = Floor(at); var end = start.AddMinutes(5);
        MarketBar? completed = null;
        if (FormingBar is { } old && old.Timestamp != end && old.Timestamp.AddMinutes(-5) >= _completeFrom)
            completed = old;
        if (FormingBar?.Timestamp == end)
            FormingBar = FormingBar with { High = Math.Max(FormingBar.High, price), Low = Math.Min(FormingBar.Low, price),
                Close = price, Volume = checked(FormingBar.Volume + trade.LastTradeVolume) };
        else FormingBar = new(market.Symbol, end, price, price, price, price, trade.LastTradeVolume);
        return completed;
    }

    public static DateTimeOffset Floor(DateTimeOffset time) => new(time.UtcTicks / (5 * TimeSpan.TicksPerMinute) * (5 * TimeSpan.TicksPerMinute), TimeSpan.Zero);
    private static DateTimeOffset Ceiling(DateTimeOffset time) => time == Floor(time) ? time : Floor(time).AddMinutes(5);
    public static decimal Number(Price? price) => decimal.TryParse(price?.Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out var value) ? value : throw new T4FeedException("Unsupported provider price format.", false);
    private static decimal TickPrice(Price? price)
    {
        var value = Number(price);
        if (value <= 0 || value > 1_000_000 || value % .25m != 0) throw new T4FeedException("Invalid tick price.", false);
        return value;
    }
    private static decimal? Side(IEnumerable<MarketDepth.Types.DepthLine> levels, bool bid)
    {
        var values = levels.Where(x => x.Volume > 0).Select(x => TickPrice(x.Price)).ToArray();
        return values.Length == 0 ? null : bid ? values.Max() : values.Min();
    }
}
