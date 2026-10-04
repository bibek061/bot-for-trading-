namespace AutopilotQuant.Core.Risk;

public record RiskState(
    int OpenPositions,
    double DailyLossPct,
    double DrawdownPct,
    bool DataFresh,
    bool BrokerConnected,
    bool LiveExecutionEnabled,
    bool PaperTrading = false);

public record RiskDecision(bool Approved, string Reason);

public record RiskLimits(
    int MaxOpenPositions,
    double DailyLossLimitPct,
    double MaxDrawdownPausePct)
{
    public static RiskLimits Default { get; } = new(1, 0.03, 0.10);
}

public static class RiskEngine
{
    public static RiskDecision Evaluate(RiskState s, RiskLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        limits ??= RiskLimits.Default;
        Validate(s, limits);

        if (!s.DataFresh) return new(false, "Market data is stale.");
        if (!s.BrokerConnected) return new(false, "Broker is disconnected.");
        if (s.OpenPositions >= limits.MaxOpenPositions) return new(false, "Maximum open positions reached.");
        if (s.DailyLossPct >= limits.DailyLossLimitPct) return new(false, "Daily loss limit reached.");
        if (s.DrawdownPct >= limits.MaxDrawdownPausePct) return new(false, "Maximum drawdown pause triggered.");
        if (!s.PaperTrading && !s.LiveExecutionEnabled)
            return new(false, "DRY RUN: live order routing disabled.");
        return new(true, "Risk checks passed.");
    }

    private static void Validate(RiskState state, RiskLimits limits)
    {
        if (state.OpenPositions < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Open positions cannot be negative.");
        if (!double.IsFinite(state.DailyLossPct) || state.DailyLossPct < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Daily loss percentage must be finite and non-negative.");
        if (!double.IsFinite(state.DrawdownPct) || state.DrawdownPct < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Drawdown percentage must be finite and non-negative.");
        if (limits.MaxOpenPositions <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum open positions must be positive.");
        if (!double.IsFinite(limits.DailyLossLimitPct) || limits.DailyLossLimitPct <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Daily loss limit must be finite and positive.");
        if (!double.IsFinite(limits.MaxDrawdownPausePct) || limits.MaxDrawdownPausePct <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum drawdown pause must be finite and positive.");
    }
}
