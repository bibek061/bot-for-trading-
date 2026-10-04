namespace AutopilotQuant.Core.Broker;

public record OrderRequest(
    string Symbol,
    string Side,
    int Quantity,
    string OrderType,
    decimal? LimitPrice,
    string Tag,
    decimal? ReferencePrice = null);
public record OrderResult(bool Ok, string? OrderId, string? Error, string? PositionId = null);

public record PositionCloseResult(bool Ok, string? OrderId, string? Error, ClosedPaperTrade? Trade);

public interface IBroker
{
    Task ConnectAsync(CancellationToken ct);
    Task<bool> IsConnectedAsync(CancellationToken ct);
    Task<decimal> GetAccountEquityAsync(CancellationToken ct);
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct);
    Task<PositionCloseResult> ClosePositionAsync(
        string positionId,
        decimal referencePrice,
        CancellationToken ct);
}
