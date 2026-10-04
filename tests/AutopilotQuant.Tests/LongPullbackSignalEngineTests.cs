using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Regimes;
using AutopilotQuant.Core.Signals;
using Xunit;

namespace AutopilotQuant.Tests;

public class LongPullbackSignalEngineTests
{
    private static IReadOnlyList<MarketBar> CreateUptrendBars()
    {
        var bars = new List<MarketBar>();
        for (var i = 0; i < 98; i++)
        {
            var close = 100m + i * 0.25m;
            bars.Add(CreateBar(i, close));
        }
        return bars;
    }

    private static MarketBar CreateBar(int index, decimal close, decimal halfRange = 0.25m) =>
        new(
            "MES",
            DateTimeOffset.UnixEpoch.AddMinutes(index * 5),
            close,
            close + halfRange,
            close - halfRange,
            close,
            100);

    [Fact]
    public void Evaluate_Emits_Long_Signal_On_Uptrend_EMA20_Reclaim()
    {
        var bars = CreateUptrendBars().ToList();
        bars.Add(CreateBar(98, 122.25m));
        bars.Add(CreateBar(99, 120.25m));
        bars.Add(CreateBar(100, 123.25m));

        var evaluation = new LongPullbackSignalEngine().Evaluate(bars);

        Assert.Equal(MarketRegime.Trend, evaluation.Regime.Regime);
        Assert.Equal(TrendDirection.Up, evaluation.Regime.Direction);
        Assert.NotNull(evaluation.Signal);
        Assert.Equal("MES", evaluation.Signal.Symbol);
        Assert.Equal("BUY", evaluation.Signal.Side);
        Assert.Equal(123.25m, evaluation.Signal.ReferencePrice);
        Assert.Equal(bars[^1].Timestamp, evaluation.Signal.BarTimestamp);
        Assert.Equal("EMA20 pullback reclaim", evaluation.Signal.Tag);
    }

    [Fact]
    public void Evaluate_Does_Not_Emit_Signal_Without_A_Pullback()
    {
        var evaluation = new LongPullbackSignalEngine().Evaluate(
            CreateUptrendBars().Concat([CreateBar(98, 124.5m), CreateBar(99, 124.75m)]).ToArray());

        Assert.Equal(MarketRegime.Trend, evaluation.Regime.Regime);
        Assert.Null(evaluation.Signal);
        Assert.Contains("did not confirm", evaluation.Reason);
    }

    [Fact]
    public void Evaluate_Does_Not_Emit_Signal_In_Chop_Or_RiskOff()
    {
        var engine = new LongPullbackSignalEngine();
        var chopBars = Enumerable.Range(0, 60).Select(i => CreateBar(i, 100m)).ToArray();
        var riskOffBars = Enumerable.Range(0, 60)
            .Select(i => CreateBar(i, 100m, 1m))
            .ToArray();

        var chop = engine.Evaluate(chopBars);
        var riskOff = engine.Evaluate(riskOffBars);

        Assert.Equal(MarketRegime.Chop, chop.Regime.Regime);
        Assert.Null(chop.Signal);
        Assert.Equal(MarketRegime.RiskOff, riskOff.Regime.Regime);
        Assert.Null(riskOff.Signal);
    }

    [Fact]
    public void Evaluate_Does_Not_Emit_Signal_In_Downtrend()
    {
        var bars = Enumerable.Range(0, 100)
            .Select(i => CreateBar(i, 150m - i * 0.25m))
            .ToArray();

        var evaluation = new LongPullbackSignalEngine().Evaluate(bars);

        Assert.Equal(MarketRegime.Trend, evaluation.Regime.Regime);
        Assert.Equal(TrendDirection.Down, evaluation.Regime.Direction);
        Assert.Null(evaluation.Signal);
        Assert.Contains("downtrend", evaluation.Reason);
    }
}
