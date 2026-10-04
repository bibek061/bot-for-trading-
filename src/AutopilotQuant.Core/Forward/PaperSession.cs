using System.Globalization;
using System.Text.Json;
using AutopilotQuant.Core.Broker;
using AutopilotQuant.Core.Instruments;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Signals;

namespace AutopilotQuant.Core.Forward;

public sealed class PaperSession
{
    private readonly PaperSessionStore _store;
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _started;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LongPullbackSignalEngine _signals = new();
    private PaperSessionState _state;
    private bool _storageFailed;

    public PaperSession(PaperSessionStore store, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetUtcNow();
        _state = store.Load();
        ValidateSettings(_state.Settings);
        _ = Broker(_state);
        // A restart never re-arms trading or resurrects unfilled entry intentions.
        _state.Paused = true;
        _state.Pending.Clear();
        _state.Quotes.Clear(); // Even a persisted quote within the permitted future-clock skew is not a new feed event.
        _state.PauseReason = "Started paused; fresh adapter quotes and explicit resume required.";
        AddEvent(_state, "Recovery", _state.PauseReason, _started);
        _store.Save(_state);
    }

    public async Task<SessionView> ViewAsync()
    {
        await _gate.WaitAsync();
        try { return await View(_state, _clock.GetUtcNow()); }
        finally { _gate.Release(); }
    }

    public Task<SessionView> ControlAsync(string action) => Change(async (s, b, now) =>
    {
        switch (action)
        {
            case "pause": Pause(s, "Paused by operator.", now); break;
            case "cancel-pending":
                s.Pending.Clear();
                AddEvent(s, "Control", "Unfilled paper entries canceled; protections retained.", now);
                break;
            case "flatten":
                Pause(s, "Flatten requested; stale symbols wait for a fresh quote.", now);
                s.FlattenRequested = true;
                break;
            case "resume":
                if (s.RiskHalted) throw new InvalidOperationException("Risk halt is active.");
                if (s.FlattenRequested) throw new InvalidOperationException("Flatten has not completed.");
                if (!s.Quotes.Values.Any(q => Fresh(q, s.Settings, now)))
                    throw new InvalidOperationException("A fresh adapter quote is required before resuming.");
                if (b.OpenPositions.Any(p => !HasFresh(s, p.Symbol, now)))
                    throw new InvalidOperationException("All open positions need fresh quotes.");
                if (!InEntryWindow(s.Settings, now))
                    throw new InvalidOperationException("Outside the configured weekday entry window.");
                s.Paused = false;
                s.PauseReason = "Paper strategy armed; waiting for a new completed-bar signal.";
                AddEvent(s, "Control", s.PauseReason, now);
                break;
            default: throw new ArgumentException("Unknown paper control.");
        }
        await Monitor(s, b, now);
    });

    public Task<SessionView> ConfigureAsync(ForwardSettings settings) => Change((s, b, now) =>
    {
        ValidateSettings(settings);
        if (!s.Paused || b.OpenPositions.Count != 0 || s.Pending.Count != 0)
            throw new InvalidOperationException("Pause and flatten before changing settings.");
        // These are account identity/baseline settings, not a way to erase losses.
        if (settings.InitialEquity != s.Settings.InitialEquity || settings.TimeZoneId != s.Settings.TimeZoneId
            || settings.SessionResetHour != s.Settings.SessionResetHour)
            throw new ArgumentException("Account baseline and session calendar cannot be changed in an existing session.");
        s.Settings = settings;
        AddEvent(s, "Settings", "Paper risk and execution settings updated while flat and paused.", now);
        return Task.CompletedTask;
    });

    public Task<SessionView> AcceptQuoteAsync(FeedQuote quote) => Change((s, b, now) => ApplyQuote(quote, s, b, now));

    public Task<SessionView> AcceptQuotesAsync(IReadOnlyList<FeedQuote> quotes) => Change(async (s, b, now) =>
    {
        if (quotes.Count is < 1 or > 128) throw new ArgumentException("Quote batch must contain 1–128 events.");
        foreach (var quote in quotes) await ApplyQuote(quote, s, b, now);
    });

    private async Task ApplyQuote(FeedQuote quote, PaperSessionState s, PaperBroker b, DateTimeOffset now)
    {
        var spec = Contract(quote.Symbol);
        ValidateContractId(quote.ContractId);
        Price(quote.Bid, spec); Price(quote.Ask, spec);
        if (quote.Ask < quote.Bid) throw new ArgumentException("Ask cannot be below bid.");
        if (quote.Timestamp > now.AddSeconds(2) || now - quote.Timestamp > TimeSpan.FromSeconds(s.Settings.QuoteMaxAgeSeconds))
            throw new ArgumentException("Quote is stale or in the future.");
        if (s.Quotes.TryGetValue(quote.Symbol, out var previous) && quote.Timestamp <= previous.Timestamp)
            throw new ArgumentException("Duplicate or out-of-order quote.");
        BindContract(s, b, quote.Symbol, quote.ContractId, now);
        s.Quotes[quote.Symbol] = quote;
        b.MarkToMarket(quote.Symbol, quote.Bid);
        await Monitor(s, b, now);
        foreach (var position in b.OpenPositions.Where(p => p.Symbol == quote.Symbol).ToArray())
        {
            if (!s.Protection.TryGetValue(position.PositionId, out var protection))
                throw new InvalidDataException("Open paper position is missing protection.");
            if (quote.Bid <= protection.StopPrice || quote.Bid >= protection.TargetPrice)
                await Close(s, b, position, quote, quote.Bid <= protection.StopPrice ? "Stop" : "Target", now);
        }
        await Monitor(s, b, now);
        if (!s.Paused && s.Pending.TryGetValue(quote.Symbol, out var pending)
            && quote.Timestamp > pending.SignalAt && quote.Timestamp > pending.ReceivedAt)
        {
            s.Pending.Remove(quote.Symbol);
            var equity = await b.GetAccountEquityAsync(default);
            var plannedRisk = (s.Settings.StopTicks + 2 * s.Settings.SlippageTicks) * spec.TickValue
                * s.Settings.Quantity + 2 * s.Settings.FeePerContract * s.Settings.Quantity;
            string? rejection = null;
            if (pending.ContractId != quote.ContractId) rejection = "Contract changed.";
            else if (now - pending.ReceivedAt > TimeSpan.FromSeconds(s.Settings.QuoteMaxAgeSeconds)) rejection = "Signal expired.";
            else if (!InEntryWindow(s.Settings, now)) rejection = "Outside entry window.";
            else if ((quote.Ask - quote.Bid) / spec.TickSize > s.Settings.MaxSpreadTicks) rejection = "Spread limit exceeded.";
            else if (b.OpenPositions.Count >= s.Settings.MaxOpenPositions || b.OpenPositions.Any(p => p.Symbol == quote.Symbol))
                rejection = "Position limit reached.";
            else if (plannedRisk > equity * s.Settings.RiskPerTradePct) rejection = "Planned stop loss exceeds per-trade budget.";
            else if (b.ClosedTrades.Count >= 5000) rejection = "Session trade archive limit reached.";
            if (rejection is not null) AddEvent(s, "Blocked", rejection, now);
            else
            {
                var order = await b.PlaceOrderAsync(new(quote.Symbol, "BUY", s.Settings.Quantity, "MARKET", null,
                    "EMA20 reclaim / next quote", quote.Ask, quote.Timestamp), default);
                if (!order.Ok) throw new InvalidOperationException(order.Error);
                var position = b.OpenPositions.Single(p => p.PositionId == order.PositionId);
                var stop = position.EntryPrice - s.Settings.StopTicks * spec.TickSize;
                if (stop <= 0) throw new ArgumentException("Stop price must be positive.");
                s.Protection[position.PositionId] = new(position.PositionId, stop,
                    position.EntryPrice + s.Settings.TargetTicks * spec.TickSize);
                // Mark long exposure to executable bid, including the spread immediately.
                b.MarkToMarket(quote.Symbol, quote.Bid);
                AddEvent(s, "Paper fill", $"BUY {position.Quantity} {quote.Symbol} at {position.EntryPrice}; simulated protection active.", now);
                if (quote.Bid <= stop)
                    await Close(s, b, position, quote, "Stop already crossed at entry", now);
                await Monitor(s, b, now);
            }
        }
    }

    public Task<SessionView> AcceptBarAsync(FeedBar frame) => Change((s, b, now) => ApplyBar(frame, s, b, now));

    public Task<SessionView> LoadWarmupAsync(IReadOnlyList<FeedBar> bars) => Change(async (s, b, now) =>
    {
        if (!s.Paused) throw new InvalidOperationException("Pause before loading historical warmup.");
        if (bars.Count > 1024 || bars.Any(bar => !bar.Warmup)) throw new ArgumentException("Warmup batch is invalid.");
        foreach (var bar in bars) await ApplyBar(bar, s, b, now);
    });

    private Task ApplyBar(FeedBar frame, PaperSessionState s, PaperBroker b, DateTimeOffset now)
    {
        var bar = frame.Bar ?? throw new ArgumentException("Bar is required.");
        var spec = Contract(bar.Symbol);
        ValidateContractId(frame.ContractId);
        foreach (var price in new[] { bar.Open, bar.High, bar.Low, bar.Close }) Price(price, spec);
        if (bar.Timestamp > now.AddSeconds(2)) throw new ArgumentException("Completed bar cannot be in the future.");
        if (bar.Timestamp.UtcTicks % (5 * TimeSpan.TicksPerMinute) != 0)
            throw new ArgumentException("Use five-minute bar END timestamps aligned to five minutes.");
        if (!frame.Warmup && now - bar.Timestamp > TimeSpan.FromSeconds(s.Settings.QuoteMaxAgeSeconds))
            throw new ArgumentException("Historical bars must be marked warmup; they cannot create orders.");
        if (frame.Warmup && !s.Paused) throw new InvalidOperationException("Pause before loading warmup history.");
        BindContract(s, b, bar.Symbol, frame.ContractId, now);
        if (!s.Histories.TryGetValue(bar.Symbol, out var history)) s.Histories[bar.Symbol] = history = [];
        if (history.Count > 0 && bar.Timestamp <= history[^1].Timestamp)
            throw new ArgumentException("Duplicate or out-of-order completed bar.");
        if (history.Count > 0 && bar.Timestamp - history[^1].Timestamp > TimeSpan.FromMinutes(5))
        {
            history.Clear();
            s.Pending.Remove(bar.Symbol);
            AddEvent(s, "Data gap", $"{bar.Symbol} history reset after a gap; indicator warmup required.", now);
        }
        history.Add(bar);
        if (history.Count > 512) history.RemoveAt(0);
        var result = _signals.Evaluate(history);
        s.Pending.Remove(bar.Symbol);
        if (!frame.Warmup && result.Signal is not null)
        {
            if (s.Paused || s.RiskHalted || !HasFresh(s, bar.Symbol, now) || !InEntryWindow(s.Settings, now))
                AddEvent(s, "Blocked", $"{bar.Symbol} signal blocked: paused, halted, stale data, or outside entry window.", now);
            else
            {
                s.Pending[bar.Symbol] = new(bar.Symbol, frame.ContractId, bar.Timestamp, now);
                AddEvent(s, "Signal", $"{bar.Symbol} EMA20 reclaim; waiting for a later bid/ask quote.", now);
            }
        }
        return Task.CompletedTask;
    }

    public Task<SessionView> MonitorAsync() => Change(Monitor);

    public Task<SessionView> ResetMarketDataAsync() => Change((s, b, now) =>
    {
        if (!s.Paused || b.OpenPositions.Count != 0 || s.Pending.Count != 0)
            throw new InvalidOperationException("Pause, cancel pending entries and flatten before changing market data.");
        s.Quotes.Clear();
        s.Histories.Clear();
        s.Contracts.Clear();
        Pause(s, "Market-data configuration changed; fresh quotes and indicator warmup required.", now);
        AddEvent(s, "Market data", s.PauseReason, now);
        return Task.CompletedTask;
    }, monitorFirst: false);

    public Task<SessionView> InvalidateMarketDataAsync(string reason) => Change((s, b, now) =>
    {
        s.Quotes.Clear();
        s.Histories.Clear();
        s.Pending.Clear();
        Pause(s, reason, now);
        AddEvent(s, "Market data", reason, now);
        // Keep bound expiries, positions and protection. Fresh quotes must resolve any exposure.
        return Task.CompletedTask;
    }, monitorFirst: false);

    private async Task Monitor(PaperSessionState s, PaperBroker b, DateTimeOffset now)
    {
        var equity = await b.GetAccountEquityAsync(default);
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(s.Settings.TimeZoneId));
        var sessionDate = DateOnly.FromDateTime(local.DateTime.AddHours(-s.Settings.SessionResetHour));
        if (s.SessionDate != sessionDate)
        {
            s.SessionDate = sessionDate;
            s.DayStartEquity = equity;
            if (s.RiskHalted && Loss(s.PeakEquity, equity) < s.Settings.MaxDrawdownPct)
            {
                s.RiskHalted = false;
                Pause(s, "New session; daily halt cleared. Explicit resume required.", now);
            }
        }
        s.PeakEquity = Math.Max(s.PeakEquity, equity);
        var breach = Loss(s.DayStartEquity, equity) >= s.Settings.DailyLossLimitPct ? "Daily loss limit reached."
            : Loss(s.PeakEquity, equity) >= s.Settings.MaxDrawdownPct ? "Maximum drawdown reached." : null;
        if (breach is not null && !s.RiskHalted)
        {
            s.RiskHalted = true;
            Pause(s, breach, now);
            s.FlattenRequested = true;
        }
        var staleExposure = b.OpenPositions.Any(p => !HasFresh(s, p.Symbol, now));
        if (!s.Paused && (staleExposure || !s.Quotes.Values.Any(q => Fresh(q, s.Settings, now))))
            Pause(s, "Quotes became stale; entries paused. Paper exits require fresh data.", now);
        foreach (var pending in s.Pending.Values.ToArray())
            if (!HasFresh(s, pending.Symbol, now) || now - pending.ReceivedAt > TimeSpan.FromSeconds(s.Settings.QuoteMaxAgeSeconds))
            {
                s.Pending.Remove(pending.Symbol);
                AddEvent(s, "Expired", $"{pending.Symbol} unfilled signal expired.", now);
            }
        if (!InEntryWindow(s.Settings, now) && (!s.Paused || b.OpenPositions.Count > 0))
        {
            Pause(s, "Outside weekday trading window; paper flatten requested.", now);
            s.FlattenRequested = true;
        }
        if (s.FlattenRequested)
        {
            foreach (var position in b.OpenPositions.ToArray())
                if (HasFresh(s, position.Symbol, now))
                    await Close(s, b, position, s.Quotes[position.Symbol], "Flatten", now);
            if (b.OpenPositions.Count == 0)
            {
                s.FlattenRequested = false;
                AddEvent(s, "Control", "Paper flatten complete; entries remain paused.", now);
            }
        }
    }

    private static async Task Close(PaperSessionState s, PaperBroker b, PaperPosition p, FeedQuote q,
        string reason, DateTimeOffset now)
    {
        var close = await b.ClosePositionAsync(p.PositionId, q.Bid, default, now);
        if (!close.Ok) throw new InvalidOperationException(close.Error);
        s.Protection.Remove(p.PositionId);
        AddEvent(s, "Paper exit", $"{reason}: {p.Symbol}, net P/L {close.Trade!.NetProfitLoss:F2}.", now);
    }

    private async Task<SessionView> Change(Func<PaperSessionState, PaperBroker, DateTimeOffset, Task> action, bool monitorFirst = true)
    {
        await _gate.WaitAsync();
        try
        {
            if (_storageFailed) throw new IOException("Storage failed; restart after repairing the data directory.");
            var before = JsonSerializer.SerializeToUtf8Bytes(_state, PaperSessionStore.Json);
            var next = JsonSerializer.Deserialize<PaperSessionState>(before, PaperSessionStore.Json)!;
            var broker = Broker(next);
            var now = _clock.GetUtcNow();
            if (monitorFirst) await Monitor(next, broker, now);
            await action(next, broker, now);
            next.Broker = broker.CaptureState();
            var after = JsonSerializer.SerializeToUtf8Bytes(next, PaperSessionStore.Json);
            if (!before.AsSpan().SequenceEqual(after))
            {
                try { _store.Save(next); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _storageFailed = true;
                    throw;
                }
                _state = next;
            }
            return await View(_state, now);
        }
        finally { _gate.Release(); }
    }

    private async Task<SessionView> View(PaperSessionState s, DateTimeOffset now)
    {
        var b = Broker(s);
        var equity = await b.GetAccountEquityAsync(default);
        return new("PAPER", s.Paused || _storageFailed, _storageFailed ? "Storage fault — restart required." : s.PauseReason,
            s.RiskHalted, equity, b.RealizedNetProfitLoss, b.UnrealizedGrossProfitLoss, b.TotalFees,
            Loss(s.DayStartEquity, equity), Loss(s.PeakEquity, equity), s.Settings, b.OpenPositions,
            s.Protection.Values.ToArray(), b.ClosedTrades.TakeLast(100).Reverse().ToArray(),
            b.Fills.TakeLast(100).Reverse().ToArray(), s.Pending.Values.ToArray(),
            new[] { "MES", "MNQ" }.Select(symbol =>
            {
                var bars = s.Histories.GetValueOrDefault(symbol) ?? [];
                var evaluation = _signals.Evaluate(bars);
                return new InstrumentView(symbol, s.Contracts.GetValueOrDefault(symbol), s.Quotes.GetValueOrDefault(symbol),
                    HasFresh(s, symbol, now), bars.TakeLast(512).ToArray(), evaluation.Reason,
                    evaluation.Regime.FastEma, evaluation.Regime.SlowEma);
            }).ToArray(), s.Events.AsEnumerable().Reverse().ToArray(), now);
    }

    private static PaperBroker Broker(PaperSessionState s) => new(new(s.Settings.InitialEquity,
        s.Settings.FeePerContract, s.Settings.SlippageTicks), s.Broker);
    private bool HasFresh(PaperSessionState s, string symbol, DateTimeOffset now) =>
        s.Quotes.TryGetValue(symbol, out var q) && Fresh(q, s.Settings, now);
    private bool Fresh(FeedQuote q, ForwardSettings settings, DateTimeOffset now) =>
        q.Timestamp >= _started && q.Timestamp <= now.AddSeconds(2)
        && now - q.Timestamp <= TimeSpan.FromSeconds(settings.QuoteMaxAgeSeconds);
    private static decimal Loss(decimal baseline, decimal equity) =>
        baseline <= 0 ? 0 : Math.Max(0, (baseline - equity) / baseline);
    private static ContractSpec Contract(string symbol) => symbol switch
    {
        "MES" => ContractSpec.MES, "MNQ" => ContractSpec.MNQ,
        _ => throw new ArgumentException("Symbol must be MES or MNQ.")
    };
    private static void Price(decimal value, ContractSpec spec)
    {
        if (value <= 0 || value > 1_000_000 || value % spec.TickSize != 0)
            throw new ArgumentException("Prices must be positive, tick-aligned, and at most 1,000,000.");
    }
    private static void ValidateContractId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(char.IsControl))
            throw new ArgumentException("An explicit provider contract ID (up to 100 characters) is required.");
    }
    private static void BindContract(PaperSessionState s, PaperBroker b, string symbol, string id, DateTimeOffset now)
    {
        if (s.Contracts.TryGetValue(symbol, out var current) && current != id)
        {
            if (!s.Paused || b.OpenPositions.Any(p => p.Symbol == symbol))
                throw new InvalidOperationException("Pause and flatten before changing the contract.");
            s.Histories.Remove(symbol); s.Pending.Remove(symbol); s.Quotes.Remove(symbol);
            AddEvent(s, "Contract", $"{symbol} contract changed; warmup required.", now);
        }
        s.Contracts[symbol] = id;
    }
    private static void Pause(PaperSessionState s, string reason, DateTimeOffset now)
    {
        if (!s.Paused || s.PauseReason != reason) AddEvent(s, "Paused", reason, now);
        s.Paused = true; s.PauseReason = reason; s.Pending.Clear();
    }
    private static void AddEvent(PaperSessionState s, string kind, string message, DateTimeOffset now)
    {
        s.Events.Add(new(now, kind, message));
        if (s.Events.Count > 200) s.Events.RemoveAt(0);
    }
    private static bool InEntryWindow(ForwardSettings settings, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId));
        var time = TimeOnly.FromDateTime(local.DateTime);
        return local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && time >= TimeOnly.ParseExact(settings.EntryStart, "HH:mm", CultureInfo.InvariantCulture)
            && time < TimeOnly.ParseExact(settings.EntryEnd, "HH:mm", CultureInfo.InvariantCulture);
    }
    public static void ValidateSettings(ForwardSettings o)
    {
        if (o.InitialEquity <= 0 || o.InitialEquity > 100_000_000 || o.FeePerContract < 0 || o.FeePerContract > 100
            || o.SlippageTicks is < 0 or > 100 || o.Quantity is < 1 or > 10 || o.MaxOpenPositions is < 1 or > 2
            || o.DailyLossLimitPct is <= 0 or > 0.25m || o.MaxDrawdownPct is <= 0 or > 0.5m
            || o.RiskPerTradePct is <= 0 or > 0.05m || o.StopTicks is < 1 or > 10000 || o.TargetTicks is < 1 or > 10000
            || o.MaxSpreadTicks is < 1 or > 100 || o.QuoteMaxAgeSeconds is < 1 or > 60 || o.SessionResetHour is < 0 or > 23)
            throw new ArgumentException("Paper settings are outside the supported limits.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(o.TimeZoneId);
        if (!TimeOnly.TryParseExact(o.EntryStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(o.EntryEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || start >= end)
            throw new ArgumentException("Entry times must be HH:mm with start before end.");
    }
}
