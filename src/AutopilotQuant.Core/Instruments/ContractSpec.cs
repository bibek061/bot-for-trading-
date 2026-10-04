namespace AutopilotQuant.Core.Instruments;

public record ContractSpec(string RootSymbol, decimal TickSize, decimal PointValue)
{
    public decimal TickValue => TickSize * PointValue;

    public static readonly ContractSpec MES = new("MES", 0.25m, 5m);
    public static readonly ContractSpec MNQ = new("MNQ", 0.25m, 2m);
}
