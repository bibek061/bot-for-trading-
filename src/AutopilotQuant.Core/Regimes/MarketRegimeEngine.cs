using AutopilotQuant.Core.MarketData;

namespace AutopilotQuant.Core.Regimes;

public enum MarketRegime
{
    InsufficientData,
    RiskOff,
    Trend,
    Chop
}

public enum TrendDirection
{
    Up,
    Down
}

public sealed record MarketRegimeOptions(
    int AtrPeriod = 14,
    int FastEmaPeriod = 20,
    int SlowEmaPeriod = 50,
    decimal RiskOffAtrPercent = 0.01m);

public sealed record MarketRegimeResult(
    MarketRegime Regime,
    TrendDirection? Direction,
    decimal? Atr,
    decimal? AtrPercent,
    decimal? FastEma,
    decimal? PreviousFastEma,
    decimal? SlowEma,
    string Reason);

public sealed class MarketRegimeEngine
{
    private readonly MarketRegimeOptions _options;

    public MarketRegimeEngine(MarketRegimeOptions? options = null)
    {
        _options = options ?? new MarketRegimeOptions();
        ValidateOptions(_options);
    }

    public MarketRegimeResult Evaluate(IReadOnlyList<MarketBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidateBars(bars);

        var requiredBars = Math.Max(_options.SlowEmaPeriod, _options.AtrPeriod + 1);
        if (bars.Count < requiredBars)
        {
            return new MarketRegimeResult(
                MarketRegime.InsufficientData,
                null,
                null,
                null,
                null,
                null,
                null,
                $"Need at least {requiredBars} bars to classify regime.");
        }

        var fastEma = CalculateEma(bars, _options.FastEmaPeriod);
        var slowEma = CalculateEma(bars, _options.SlowEmaPeriod);
        var atr = CalculateAtr(bars, _options.AtrPeriod);
        var atrPercent = atr / bars[^1].Close;

        if (atrPercent >= _options.RiskOffAtrPercent)
        {
            return new MarketRegimeResult(
                MarketRegime.RiskOff,
                null,
                atr,
                atrPercent,
                fastEma[^1],
                fastEma[^2],
                slowEma[^1],
                "ATR as a percentage of close is at or above the risk-off threshold.");
        }

        var fastEmaSlope = fastEma[^1] - fastEma[^2];
        if (fastEma[^1] > slowEma[^1] && fastEmaSlope > 0)
        {
            return new MarketRegimeResult(
                MarketRegime.Trend,
                TrendDirection.Up,
                atr,
                atrPercent,
                fastEma[^1],
                fastEma[^2],
                slowEma[^1],
                "Fast EMA is above slow EMA and rising.");
        }

        if (fastEma[^1] < slowEma[^1] && fastEmaSlope < 0)
        {
            return new MarketRegimeResult(
                MarketRegime.Trend,
                TrendDirection.Down,
                atr,
                atrPercent,
                fastEma[^1],
                fastEma[^2],
                slowEma[^1],
                "Fast EMA is below slow EMA and falling.");
        }

        return new MarketRegimeResult(
            MarketRegime.Chop,
            null,
            atr,
            atrPercent,
            fastEma[^1],
            fastEma[^2],
            slowEma[^1],
            "EMA alignment and slope do not confirm a trend.");
    }

    private static decimal[] CalculateEma(IReadOnlyList<MarketBar> bars, int period)
    {
        var values = new decimal[bars.Count];
        decimal seed = 0;
        for (var i = 0; i < period; i++)
            seed += bars[i].Close;

        var ema = seed / period;
        values[period - 1] = ema;
        var multiplier = 2m / (period + 1);
        for (var i = period; i < bars.Count; i++)
        {
            ema = (bars[i].Close - ema) * multiplier + ema;
            values[i] = ema;
        }

        return values;
    }

    private static decimal CalculateAtr(IReadOnlyList<MarketBar> bars, int period)
    {
        decimal atr = 0;
        for (var i = 1; i <= period; i++)
            atr += TrueRange(bars[i], bars[i - 1].Close);
        atr /= period;

        for (var i = period + 1; i < bars.Count; i++)
            atr = ((atr * (period - 1)) + TrueRange(bars[i], bars[i - 1].Close)) / period;

        return atr;
    }

    private static decimal TrueRange(MarketBar bar, decimal previousClose) =>
        Math.Max(
            bar.High - bar.Low,
            Math.Max(
                Math.Abs(bar.High - previousClose),
                Math.Abs(bar.Low - previousClose)));

    private static void ValidateOptions(MarketRegimeOptions options)
    {
        if (options.AtrPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ATR period must be positive.");
        if (options.FastEmaPeriod <= 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Fast EMA period must be greater than one.");
        if (options.SlowEmaPeriod <= options.FastEmaPeriod)
            throw new ArgumentOutOfRangeException(nameof(options), "Slow EMA period must be greater than fast EMA period.");
        if (options.RiskOffAtrPercent <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Risk-off ATR percentage must be positive.");
    }

    private static void ValidateBars(IReadOnlyList<MarketBar> bars)
    {
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i] ?? throw new ArgumentException("Bars cannot contain null entries.", nameof(bars));
            if (string.IsNullOrWhiteSpace(bar.Symbol))
                throw new ArgumentException("Bar symbol is required.", nameof(bars));
            if (i > 0 && !string.Equals(bar.Symbol, bars[0].Symbol, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("All bars in a series must use the same symbol.", nameof(bars));
            if (bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0)
                throw new ArgumentOutOfRangeException(nameof(bars), "Bar prices must be positive.");
            if (bar.High < bar.Low
                || bar.High < bar.Open
                || bar.High < bar.Close
                || bar.Low > bar.Open
                || bar.Low > bar.Close)
                throw new ArgumentException("Bar high/low values must contain the open and close.", nameof(bars));
            if (bar.Volume < 0)
                throw new ArgumentOutOfRangeException(nameof(bars), "Bar volume cannot be negative.");
            if (i > 0 && bar.Timestamp <= bars[i - 1].Timestamp)
                throw new ArgumentException("Bars must be in strictly increasing timestamp order.", nameof(bars));
        }
    }
}
