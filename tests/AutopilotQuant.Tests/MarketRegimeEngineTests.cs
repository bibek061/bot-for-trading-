using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Regimes;
using Xunit;

namespace AutopilotQuant.Tests;

public class MarketRegimeEngineTests
{
    private static IReadOnlyList<MarketBar> MakeBars(
        int count,
        Func<int, decimal> closeAt,
        Func<int, decimal>? halfRangeAt = null)
    {
        var bars = new List<MarketBar>(count);
        for (var i = 0; i < count; i++)
        {
            var close = closeAt(i);
            var halfRange = halfRangeAt?.Invoke(i) ?? 0.25m;
            bars.Add(new MarketBar(
                "MES",
                DateTimeOffset.UnixEpoch.AddMinutes(i * 5),
                close,
                close + halfRange,
                close - halfRange,
                close,
                100));
        }
        return bars;
    }

    [Fact]
    public void Evaluate_Returns_InsufficientData_Before_EMA_Warmup()
    {
        var engine = new MarketRegimeEngine();
        var bars = MakeBars(49, _ => 100m);

        var result = engine.Evaluate(bars);

        Assert.Equal(MarketRegime.InsufficientData, result.Regime);
        Assert.Null(result.Atr);
        Assert.Null(result.FastEma);
        Assert.Contains("50 bars", result.Reason);
    }

    [Fact]
    public void Evaluate_Classifies_Uptrend_When_Emas_Align_And_Fast_Ema_Rises()
    {
        var engine = new MarketRegimeEngine();
        var bars = MakeBars(100, i => 100m + i * 0.25m);

        var result = engine.Evaluate(bars);

        Assert.Equal(MarketRegime.Trend, result.Regime);
        Assert.Equal(TrendDirection.Up, result.Direction);
        Assert.True(result.FastEma > result.SlowEma);
        Assert.True(result.AtrPercent < 0.01m);
    }

    [Fact]
    public void Evaluate_Classifies_Downtrend_When_Emas_Align_And_Fast_Ema_Falls()
    {
        var engine = new MarketRegimeEngine();
        var bars = MakeBars(100, i => 150m - i * 0.25m);

        var result = engine.Evaluate(bars);

        Assert.Equal(MarketRegime.Trend, result.Regime);
        Assert.Equal(TrendDirection.Down, result.Direction);
        Assert.True(result.FastEma < result.SlowEma);
        Assert.True(result.AtrPercent < 0.01m);
    }

    [Fact]
    public void Evaluate_Classifies_RiskOff_Before_Trend()
    {
        var engine = new MarketRegimeEngine();
        var bars = MakeBars(60, _ => 100m, _ => 1m);

        var result = engine.Evaluate(bars);

        Assert.Equal(MarketRegime.RiskOff, result.Regime);
        Assert.Null(result.Direction);
        Assert.True(result.AtrPercent >= 0.01m);
    }

    [Fact]
    public void Evaluate_Classifies_Flat_Market_As_Chop()
    {
        var engine = new MarketRegimeEngine();
        var bars = MakeBars(60, _ => 100m);

        var result = engine.Evaluate(bars);

        Assert.Equal(MarketRegime.Chop, result.Regime);
        Assert.Null(result.Direction);
        Assert.Equal(100m, result.FastEma);
        Assert.Equal(100m, result.SlowEma);
    }

    [Fact]
    public void Evaluate_Uses_Custom_RiskOff_Threshold()
    {
        var engine = new MarketRegimeEngine(new MarketRegimeOptions(RiskOffAtrPercent: 0.03m));
        var bars = MakeBars(60, _ => 100m, _ => 1m);

        var result = engine.Evaluate(bars);

        Assert.Equal(MarketRegime.Chop, result.Regime);
        Assert.True(result.AtrPercent < 0.03m);
    }

    [Theory]
    [InlineData(0, 20, 50, 0.01)]
    [InlineData(14, 1, 50, 0.01)]
    [InlineData(14, 20, 20, 0.01)]
    [InlineData(14, 20, 50, 0)]
    public void Constructor_Rejects_Invalid_Options(
        int atrPeriod,
        int fastEmaPeriod,
        int slowEmaPeriod,
        double riskOffAtrPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MarketRegimeEngine(new MarketRegimeOptions(
                atrPeriod,
                fastEmaPeriod,
                slowEmaPeriod,
                (decimal)riskOffAtrPercent)));
    }

    [Fact]
    public void Evaluate_Rejects_OutOfOrder_Bars()
    {
        var bars = MakeBars(51, _ => 100m).ToArray();
        (bars[10], bars[11]) = (bars[11], bars[10]);

        Assert.Throws<ArgumentException>(() => new MarketRegimeEngine().Evaluate(bars));
    }

    [Fact]
    public void Evaluate_Rejects_Invalid_Ohlc_And_Volume()
    {
        var invalidRange = MakeBars(50, _ => 100m).ToArray();
        invalidRange[0] = invalidRange[0] with { High = 99m };
        var negativeVolume = MakeBars(50, _ => 100m).ToArray();
        negativeVolume[0] = negativeVolume[0] with { Volume = -1 };

        Assert.Throws<ArgumentException>(() => new MarketRegimeEngine().Evaluate(invalidRange));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarketRegimeEngine().Evaluate(negativeVolume));
    }
}
