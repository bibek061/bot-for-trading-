using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Regimes;

namespace AutopilotQuant.Core.Signals;

public sealed record EntrySignal(
    string Symbol,
    string Side,
    decimal ReferencePrice,
    DateTimeOffset BarTimestamp,
    string Tag);

public sealed record SignalEvaluation(
    MarketRegimeResult Regime,
    EntrySignal? Signal,
    string Reason);

public sealed class LongPullbackSignalEngine
{
    private readonly MarketRegimeEngine _regimeEngine;

    public LongPullbackSignalEngine(MarketRegimeEngine? regimeEngine = null)
    {
        _regimeEngine = regimeEngine ?? new MarketRegimeEngine();
    }

    public SignalEvaluation Evaluate(IReadOnlyList<MarketBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var regime = _regimeEngine.Evaluate(bars);
        if (regime.Regime != MarketRegime.Trend
            || regime.Direction != TrendDirection.Up)
        {
            return new SignalEvaluation(
                regime,
                null,
                regime.Regime == MarketRegime.Trend
                    ? "Long-only signal engine does not enter in a downtrend."
                    : $"No entry: market regime is {regime.Regime}.");
        }

        if (bars.Count < 2
            || regime.FastEma is not decimal fastEma
            || regime.PreviousFastEma is not decimal previousFastEma)
        {
            return new SignalEvaluation(regime, null, "Not enough indicator history to evaluate an EMA reclaim.");
        }

        var previousBar = bars[^2];
        var currentBar = bars[^1];
        var pulledBack = previousBar.Low <= previousFastEma
            && previousBar.Close <= previousFastEma;
        var reclaimed = currentBar.Close > fastEma;
        if (!pulledBack || !reclaimed)
        {
            return new SignalEvaluation(
                regime,
                null,
                "No entry: the latest completed bar did not confirm an EMA20 pullback and reclaim.");
        }

        var signal = new EntrySignal(
            currentBar.Symbol,
            "BUY",
            currentBar.Close,
            currentBar.Timestamp,
            "EMA20 pullback reclaim");
        return new SignalEvaluation(regime, signal, "Uptrend EMA20 pullback reclaim confirmed.");
    }
}
