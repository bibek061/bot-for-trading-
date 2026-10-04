using AutopilotQuant.Core.Broker;

namespace AutopilotQuant.T4;

// Safe placeholder. Real T4 API wiring is intentionally not enabled yet.
public sealed class T4Broker : IBroker
{
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> IsConnectedAsync(CancellationToken ct) => Task.FromResult(false);
    public Task<decimal> GetAccountEquityAsync(CancellationToken ct) => Task.FromResult(0m);

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct) =>
        Task.FromResult(new OrderResult(false, null, "DRY RUN: T4 live order routing is not wired."));

    public Task<PositionCloseResult> ClosePositionAsync(
        string positionId,
        decimal referencePrice,
        CancellationToken ct) =>
        Task.FromResult(new PositionCloseResult(false, null, "DRY RUN: T4 live order routing is not wired.", null));
}
