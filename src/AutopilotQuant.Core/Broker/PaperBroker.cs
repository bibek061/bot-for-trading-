using AutopilotQuant.Core.Instruments;

namespace AutopilotQuant.Core.Broker;

public sealed record PaperBrokerOptions(
    decimal InitialEquity,
    decimal FeePerContract,
    int SlippageTicks,
    bool AllowShorts = false);

public sealed record PaperFill(
    string OrderId,
    string PositionId,
    string Symbol,
    string Side,
    int Quantity,
    decimal ReferencePrice,
    decimal FillPrice,
    decimal Fee,
    string Tag,
    DateTimeOffset FilledAtUtc);

public sealed record PaperPosition(
    string PositionId,
    string EntryOrderId,
    string Symbol,
    string Side,
    int Quantity,
    decimal EntryPrice,
    decimal EntryFee,
    string Tag,
    DateTimeOffset OpenedAtUtc);

public sealed record ClosedPaperTrade(
    string PositionId,
    string EntryOrderId,
    string ExitOrderId,
    string Symbol,
    string Side,
    int Quantity,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal GrossProfitLoss,
    decimal EntryFee,
    decimal ExitFee,
    decimal NetProfitLoss,
    string Tag,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset ClosedAtUtc);

public sealed class PaperBroker : IBroker
{
    private readonly PaperBrokerOptions _options;
    private readonly object _sync = new();
    private readonly List<PaperFill> _fills = [];
    private readonly Dictionary<string, PaperPosition> _openPositions = new(StringComparer.Ordinal);
    private readonly List<ClosedPaperTrade> _closedTrades = [];
    private readonly Dictionary<string, decimal> _marketPrices = new(StringComparer.Ordinal);
    private decimal _totalFees;
    private decimal _realizedGrossProfitLoss;

    public PaperBroker(PaperBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InitialEquity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Initial equity must be positive.");
        if (options.FeePerContract < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Fee per contract cannot be negative.");
        if (options.SlippageTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Slippage ticks cannot be negative.");

        _options = options;
    }

    public IReadOnlyList<PaperFill> Fills
    {
        get
        {
            lock (_sync)
                return _fills.ToArray();
        }
    }

    public IReadOnlyList<PaperPosition> OpenPositions
    {
        get
        {
            lock (_sync)
                return _openPositions.Values.ToArray();
        }
    }

    public IReadOnlyList<ClosedPaperTrade> ClosedTrades
    {
        get
        {
            lock (_sync)
                return _closedTrades.ToArray();
        }
    }

    public decimal TotalFees
    {
        get
        {
            lock (_sync)
                return _totalFees;
        }
    }

    public decimal RealizedGrossProfitLoss
    {
        get
        {
            lock (_sync)
                return _realizedGrossProfitLoss;
        }
    }

    public decimal RealizedNetProfitLoss
    {
        get
        {
            lock (_sync)
                return _closedTrades.Sum(trade => trade.NetProfitLoss);
        }
    }

    public decimal UnrealizedGrossProfitLoss
    {
        get
        {
            lock (_sync)
                return CalculateUnrealizedGrossProfitLoss();
        }
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<bool> IsConnectedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public Task<decimal> GetAccountEquityAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var equity = _options.InitialEquity
                - _totalFees
                + _realizedGrossProfitLoss
                + CalculateUnrealizedGrossProfitLoss();
            return Task.FromResult(equity);
        }
    }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var rejection = ValidateOrder(request, out var contract, out var side);
        if (rejection is not null)
            return Task.FromResult(new OrderResult(false, null, rejection));

        var referencePrice = request.ReferencePrice!.Value;
        var slippage = contract!.TickSize * _options.SlippageTicks;
        if (side == "BUY" && referencePrice > decimal.MaxValue - slippage)
            return Task.FromResult(new OrderResult(false, null, "Simulated fill price is outside the supported range."));
        var fillPrice = side == "BUY" ? referencePrice + slippage : referencePrice - slippage;
        if (fillPrice <= 0)
            return Task.FromResult(new OrderResult(false, null, "Simulated fill price must be positive."));

        lock (_sync)
        {
            if (!CanCoverFees(request.Quantity))
                return Task.FromResult(new OrderResult(false, null, "Insufficient simulated equity to cover fees."));

            var now = request.ExecutionTimestamp ?? DateTimeOffset.UtcNow;
            var orderId = Guid.NewGuid().ToString("N");
            var positionId = Guid.NewGuid().ToString("N");
            var fee = _options.FeePerContract * request.Quantity;
            var normalizedTag = request.Tag ?? string.Empty;
            _fills.Add(new PaperFill(
                orderId,
                positionId,
                contract.RootSymbol,
                side,
                request.Quantity,
                referencePrice,
                fillPrice,
                fee,
                normalizedTag,
                now));
            _openPositions.Add(positionId, new PaperPosition(
                positionId,
                orderId,
                contract.RootSymbol,
                side,
                request.Quantity,
                fillPrice,
                fee,
                normalizedTag,
                now));
            _marketPrices[contract.RootSymbol] = referencePrice;
            _totalFees += fee;
            return Task.FromResult(new OrderResult(true, orderId, null, positionId));
        }
    }

    public Task<PositionCloseResult> ClosePositionAsync(
        string positionId,
        decimal referencePrice,
        CancellationToken ct,
        DateTimeOffset? executionTimestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionId);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_openPositions.TryGetValue(positionId, out var position))
                return Task.FromResult(new PositionCloseResult(
                    false, null, "Paper position was not found or is already closed.", null));

            var contract = GetContract(position.Symbol);
            var priceError = ValidateReferencePrice(referencePrice, contract);
            if (priceError is not null)
                return Task.FromResult(new PositionCloseResult(false, null, priceError, null));

            var closingSide = position.Side == "BUY" ? "SELL" : "BUY";
            var slippage = contract.TickSize * _options.SlippageTicks;
            if (closingSide == "BUY" && referencePrice > decimal.MaxValue - slippage)
                return Task.FromResult(new PositionCloseResult(
                    false, null, "Simulated fill price is outside the supported range.", null));
            var fillPrice = closingSide == "BUY"
                ? referencePrice + slippage
                : referencePrice - slippage;
            if (fillPrice <= 0)
                return Task.FromResult(new PositionCloseResult(
                    false, null, "Simulated fill price must be positive.", null));
            var now = executionTimestamp ?? DateTimeOffset.UtcNow;
            var orderId = Guid.NewGuid().ToString("N");
            var exitFee = _options.FeePerContract * position.Quantity;
            var grossPnl = CalculateGrossProfitLoss(position, fillPrice, contract);
            var trade = new ClosedPaperTrade(
                position.PositionId,
                position.EntryOrderId,
                orderId,
                position.Symbol,
                position.Side,
                position.Quantity,
                position.EntryPrice,
                fillPrice,
                grossPnl,
                position.EntryFee,
                exitFee,
                grossPnl - position.EntryFee - exitFee,
                position.Tag,
                position.OpenedAtUtc,
                now);

            _fills.Add(new PaperFill(
                orderId,
                position.PositionId,
                position.Symbol,
                closingSide,
                position.Quantity,
                referencePrice,
                fillPrice,
                exitFee,
                position.Tag,
                now));
            _openPositions.Remove(positionId);
            _closedTrades.Add(trade);
            _marketPrices[position.Symbol] = referencePrice;
            _totalFees += exitFee;
            _realizedGrossProfitLoss += grossPnl;
            return Task.FromResult(new PositionCloseResult(true, orderId, null, trade));
        }
    }

    public void MarkToMarket(string symbol, decimal price)
    {
        var contract = GetContract(symbol);
        var priceError = ValidateReferencePrice(price, contract);
        if (priceError is not null)
            throw new ArgumentOutOfRangeException(nameof(price), priceError);

        lock (_sync)
            _marketPrices[contract.RootSymbol] = price;
    }

    private bool CanCoverFees(int quantity)
    {
        var currentEquity = _options.InitialEquity
            - _totalFees
            + _realizedGrossProfitLoss
            + CalculateUnrealizedGrossProfitLoss();
        return currentEquity >= 0 && _options.FeePerContract <= currentEquity / quantity;
    }

    private decimal CalculateUnrealizedGrossProfitLoss()
    {
        decimal total = 0;
        foreach (var position in _openPositions.Values)
        {
            if (_marketPrices.TryGetValue(position.Symbol, out var marketPrice))
                total += CalculateGrossProfitLoss(position, marketPrice, GetContract(position.Symbol));
        }
        return total;
    }

    private static decimal CalculateGrossProfitLoss(
        PaperPosition position,
        decimal exitPrice,
        ContractSpec contract)
    {
        var priceDifference = position.Side == "BUY"
            ? exitPrice - position.EntryPrice
            : position.EntryPrice - exitPrice;
        return priceDifference * position.Quantity * contract.PointValue;
    }

    private string? ValidateOrder(
        OrderRequest request,
        out ContractSpec? contract,
        out string side)
    {
        contract = request.Symbol is null ? null : GetContractOrNull(request.Symbol);
        side = request.Side?.ToUpperInvariant() ?? string.Empty;

        if (contract is null)
            return "Unsupported paper-trading symbol.";
        if (side is not ("BUY" or "SELL"))
            return "Order side must be BUY or SELL.";
        if (side == "SELL" && !_options.AllowShorts)
            return "Short exposure is locked.";
        if (request.Quantity <= 0)
            return "Order quantity must be positive.";
        if (!string.Equals(request.OrderType, "MARKET", StringComparison.OrdinalIgnoreCase))
            return "Paper broker supports market orders only.";
        if (request.LimitPrice is not null)
            return "Market orders cannot include a limit price.";
        if (request.ReferencePrice is not decimal referencePrice)
            return "A positive reference price is required for paper fills.";

        return ValidateReferencePrice(referencePrice, contract);
    }

    private static string? ValidateReferencePrice(decimal price, ContractSpec contract)
    {
        if (price <= 0)
            return "A positive reference price is required for paper fills.";
        if (price % contract.TickSize != 0)
            return "Reference price must align to the contract tick size.";
        return null;
    }

    private static ContractSpec GetContract(string? symbol) =>
        GetContractOrNull(symbol) ?? throw new ArgumentException("Unsupported paper-trading symbol.", nameof(symbol));

    private static ContractSpec? GetContractOrNull(string? symbol) =>
        symbol?.ToUpperInvariant() switch
        {
            "MES" => ContractSpec.MES,
            "MNQ" => ContractSpec.MNQ,
            _ => null
        };
}
