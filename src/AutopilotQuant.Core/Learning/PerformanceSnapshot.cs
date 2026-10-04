namespace AutopilotQuant.Core.Learning;

public record RollingPerformance(
    int TradesClosed,
    double WinRate,
    double GrossProfit,
    double GrossLoss)
{
    public double ProfitFactor =>
        GrossLoss <= 0 ? (GrossProfit > 0 ? double.PositiveInfinity : 0) : GrossProfit / GrossLoss;
}
