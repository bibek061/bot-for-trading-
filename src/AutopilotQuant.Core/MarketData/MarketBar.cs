namespace AutopilotQuant.Core.MarketData;

public sealed record MarketBar(
    string Symbol,
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);
