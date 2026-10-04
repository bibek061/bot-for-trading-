using AutopilotQuant.Core.Learning;

namespace AutopilotQuant.Core.Risk;

public record GateDecision(bool Unlocked, string Reason);

public static class FeatureGate
{
    public static GateDecision CanUnlockShortExposure(RollingPerformance p)
    {
        if (p.TradesClosed < 12) return new(false, "Need at least 12 closed trades in 30 days.");
        if (p.WinRate < 0.60) return new(false, "30-day win rate is below 60%.");
        if (p.ProfitFactor < 1.15) return new(false, "30-day profit factor is below 1.15.");
        return new(true, "30-day short-exposure guardrails met.");
    }
}
