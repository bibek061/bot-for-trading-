using AutopilotQuant.Core.Learning;
using AutopilotQuant.Core.Risk;
using Xunit;

namespace AutopilotQuant.Tests;

public class FeatureGateTests
{
    [Fact]
    public void Unlocks_When_ThirtyDay_Guardrails_Are_Met()
    {
        var p = new RollingPerformance(20, 0.65, 148, 100);
        Assert.True(FeatureGate.CanUnlockShortExposure(p).Unlocked);
    }

    [Fact]
    public void Stays_Locked_When_ProfitFactor_Is_Too_Low()
    {
        var p = new RollingPerformance(13, 0.69, 94, 100);
        Assert.False(FeatureGate.CanUnlockShortExposure(p).Unlocked);
    }
}
