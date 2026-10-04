using AutopilotQuant.Core.Risk;
using Xunit;

namespace AutopilotQuant.Tests;

public class RiskEngineTests
{
    private static readonly RiskLimits Limits = new(1, 0.03, 0.10);

    [Fact]
    public void DryRun_Blocks_Live_Order()
    {
        var s = new RiskState(0, 0, 0, true, true, false);
        var d = RiskEngine.Evaluate(s, Limits);
        Assert.False(d.Approved);
        Assert.Contains("DRY RUN", d.Reason);
    }

    [Fact]
    public void Paper_Mode_Allows_Risk_Checks_Without_Enabling_Live_Execution()
    {
        var state = new RiskState(0, 0, 0, true, true, false, PaperTrading: true);

        var decision = RiskEngine.Evaluate(state, Limits);

        Assert.True(decision.Approved);
    }

    [Theory]
    [InlineData(0, 0, 0, false, true, true, "Market data is stale.")]
    [InlineData(0, 0, 0, true, false, true, "Broker is disconnected.")]
    [InlineData(1, 0, 0, true, true, true, "Maximum open positions reached.")]
    [InlineData(0, 0.03, 0, true, true, true, "Daily loss limit reached.")]
    [InlineData(0, 0, 0.10, true, true, true, "Maximum drawdown pause triggered.")]
    public void Evaluate_Rejects_When_A_Safety_Check_Fails(
        int openPositions,
        double dailyLossPct,
        double drawdownPct,
        bool dataFresh,
        bool brokerConnected,
        bool liveExecutionEnabled,
        string expectedReason)
    {
        var state = new RiskState(
            openPositions,
            dailyLossPct,
            drawdownPct,
            dataFresh,
            brokerConnected,
            liveExecutionEnabled);

        var decision = RiskEngine.Evaluate(state, Limits);

        Assert.False(decision.Approved);
        Assert.Equal(expectedReason, decision.Reason);
    }

    [Fact]
    public void Evaluate_Uses_Supplied_Limits()
    {
        var state = new RiskState(1, 0.02, 0.05, true, true, true);
        var limits = new RiskLimits(2, 0.04, 0.08);

        var decision = RiskEngine.Evaluate(state, limits);

        Assert.True(decision.Approved);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(-0.01, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(0, -0.01)]
    public void Evaluate_Rejects_Invalid_Risk_State(double dailyLossPct, double drawdownPct)
    {
        var state = new RiskState(0, dailyLossPct, drawdownPct, true, true, true);

        Assert.Throws<ArgumentOutOfRangeException>(() => RiskEngine.Evaluate(state, Limits));
    }

    [Fact]
    public void Evaluate_Rejects_Invalid_Limits()
    {
        var state = new RiskState(0, 0, 0, true, true, true);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => RiskEngine.Evaluate(state, new RiskLimits(0, 0.03, 0.10)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RiskEngine.Evaluate(state, new RiskLimits(1, 0, 0.10)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RiskEngine.Evaluate(state, new RiskLimits(1, 0.03, double.NaN)));
    }
}
