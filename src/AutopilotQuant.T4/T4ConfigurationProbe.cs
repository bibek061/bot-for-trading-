using System.Globalization;
using System.Net.WebSockets;
using AutopilotQuant.T4.Protocol;
using Google.Protobuf;

namespace AutopilotQuant.T4;

public sealed record T4Market(string Symbol, string ExchangeId, string ProductId, string MarketId);
public sealed record T4MarketCheck(string Symbol, bool DefinitionVerified, bool FreshQuoteObserved, string Status);
public sealed record T4ProbeResult(DateTimeOffset CheckedAt, bool Authenticated, string Outcome,
    string Summary, IReadOnlyList<T4MarketCheck> Markets);

public interface IT4ProbeTransport : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task SendAsync(byte[] message, CancellationToken cancellationToken);
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
}

// A bounded diagnostic connection, not a running data feed. No data reaches the paper account.
public sealed class T4ConfigurationProbe(Func<IT4ProbeTransport>? transportFactory = null, TimeProvider? clock = null)
{
    public const string SimulatorEndpoint = "wss://wss-sim.t4login.com/v2";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<IT4ProbeTransport> _transportFactory = transportFactory ?? (() => new T4ProbeTransport());

    public async Task<T4ProbeResult> CheckAsync(string apiKey, IReadOnlyList<T4Market> markets,
        int quoteMaxAgeSeconds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A server-side T4 API key is required.");
        if (markets.Count is < 1 or > 2 || markets.Any(m => m.Symbol is not ("MES" or "MNQ")
            || string.IsNullOrWhiteSpace(m.ExchangeId) || string.IsNullOrWhiteSpace(m.ProductId) || string.IsNullOrWhiteSpace(m.MarketId))
            || markets.Select(m => m.MarketId).Distinct(StringComparer.Ordinal).Count() != markets.Count)
            throw new ArgumentException("Select one or two distinct MES/MNQ contracts.");
        if (quoteMaxAgeSeconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(quoteMaxAgeSeconds));
        // Finish before the documented 20-second heartbeat interval. Every check uses a fresh login.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var authenticated = false;
        var checks = markets.ToDictionary(m => m.MarketId,
            m => new T4MarketCheck(m.Symbol, false, false, "Awaiting contract definition and current quote."));
        var terminal = new HashSet<string>(StringComparer.Ordinal);
        T4ProbeResult Result(string outcome, string summary) => new(_clock.GetUtcNow(), authenticated, outcome,
            summary, markets.Select(m => checks[m.MarketId]).ToArray());
        using var transport = _transportFactory();
        try
        {
            await transport.ConnectAsync(ct);
            await transport.SendAsync(new ClientMessage { LoginRequest = new() { ApiKey = apiKey, PriceFormat = 1 } }.ToByteArray(), ct);
            // The count also bounds a peer that sends irrelevant messages faster than the timeout.
            for (var count = 0; count < 4096; count++)
            {
                var message = ServerMessage.Parser.ParseFrom(await transport.ReceiveAsync(ct));
                if (message.LoginResponse is { } login)
                {
                    if (login.Result != 0 || string.IsNullOrWhiteSpace(login.SessionId) || authenticated)
                        return Result("failed", $"T4 login was not accepted (result {login.Result}). Check simulator access and API-key permissions.");
                    authenticated = true;
                    foreach (var market in markets)
                    {
                        // Codes are enums, not a bit mask. Delayed, unset and unknown types fail closed.
                        if (!login.Exchanges.Any(e => e.ExchangeId == market.ExchangeId && e.MarketDataType is 1 or 2 or 4))
                        {
                            checks[market.MarketId] = checks[market.MarketId] with { Status = "Current data entitlement is missing for this exchange." };
                            terminal.Add(market.MarketId);
                            continue;
                        }
                        await transport.SendAsync(new ClientMessage { MarketSubscribe = new() {
                            ExchangeId = market.ExchangeId, ContractId = market.ProductId, MarketId = market.MarketId,
                            Quotes = 1, Ticker = false
                        } }.ToByteArray(), ct);
                    }
                }
                else if (authenticated)
                {
                    if (message.MarketSubscribeReject is { } reject && checks.ContainsKey(reject.MarketId))
                    {
                        checks[reject.MarketId] = checks[reject.MarketId] with { FreshQuoteObserved = false, Status = $"Subscription rejected (market mode {reject.Mode})." };
                        terminal.Add(reject.MarketId);
                    }
                    if (message.MarketDetails is { } details && checks.TryGetValue(details.MarketId, out var check) && !terminal.Contains(details.MarketId))
                    {
                        var market = markets.Single(m => m.MarketId == details.MarketId);
                        var valid = details.ExchangeId == market.ExchangeId && details.ContractId == market.ProductId
                            && details.ContractType == 5 && details.StrategyType == 0 && !details.Disabled
                            && Value(details.MinPriceIncrement) == .25m && Value(details.PointValue) == (market.Symbol == "MES" ? 5m : 2m)
                            && details.LastTradingDate is not null && details.LastTradingDate.ToDateTimeOffset() > _clock.GetUtcNow();
                        checks[details.MarketId] = check with { DefinitionVerified = valid,
                            Status = valid ? "Contract verified; awaiting a current two-sided quote." : "Contract identity, tick size, point value or expiry did not match." };
                        if (!valid) terminal.Add(details.MarketId);
                    }
                    if (message.MarketDepth is { } depth) Inspect(depth, false);
                    if (message.MarketSnapshot is { } snapshot)
                        foreach (var item in snapshot.Messages)
                            if (item.MarketDepth is { } snapshotDepth && snapshotDepth.MarketId == snapshot.MarketId)
                                Inspect(snapshotDepth, snapshot.Delayed || snapshot.Mode != 2);
                }
                if (authenticated && checks.All(pair => terminal.Contains(pair.Key) || pair.Value.DefinitionVerified && pair.Value.FreshQuoteObserved))
                {
                    var complete = terminal.Count == 0;
                    return Result(complete ? "verified" : "incomplete", complete
                        ? "Simulator login, selected contracts and current quotes verified. Diagnostic closed; use Connect feed to start continuous streaming."
                        : "Simulator login succeeded, but one or more selected markets are not ready. Diagnostic connection closed.");
                }
            }
            return Result("incomplete", "Diagnostic message limit reached. Streaming readiness was not established.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result("incomplete", authenticated
                ? "Login succeeded; the 15-second check ended before all contracts and current quotes were verified. The market may be closed."
                : "The 15-second connection check timed out before login was verified.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidProtocolBufferException or ArgumentException or InvalidOperationException)
        {
            // Provider messages can contain identities or echoed credentials. Never return/log their text.
            return Result("failed", "T4 connection or response could not be verified. Check network access, simulator credentials and selected contracts.");
        }

        void Inspect(MarketDepth depth, bool unavailable)
        {
            if (!checks.TryGetValue(depth.MarketId, out var check) || terminal.Contains(depth.MarketId)) return;
            if (depth.Delayed || unavailable || depth.Mode != 2 || (depth.Flags & (32 | 64 | 2048)) != 0)
            {
                checks[depth.MarketId] = check with { FreshQuoteObserved = false, Status = "Market is closed, delayed, recovering or unavailable; current quotes not verified." };
                return;
            }
            // A single diagnostic frame must contain both sides. Never retimestamp a cached side.
            var at = depth.Time?.ToDateTimeOffset();
            var bids = depth.Bids.Where(b => b.Volume > 0).Select(b => Value(b.Price)).ToArray();
            var asks = depth.Offers.Where(b => b.Volume > 0).Select(b => Value(b.Price)).ToArray();
            if (at is null || at > _clock.GetUtcNow().AddSeconds(2) || _clock.GetUtcNow() - at > TimeSpan.FromSeconds(quoteMaxAgeSeconds)
                || bids.Length == 0 || asks.Length == 0 || bids.Concat(asks).Any(v => v <= 0 || v > 1_000_000 || v % .25m != 0)
                || asks.Min() < bids.Max()) return;
            checks[depth.MarketId] = check with { FreshQuoteObserved = true,
                Status = check.DefinitionVerified ? "Contract and current two-sided quote verified during this check." : "Quote observed; awaiting contract definition." };
        }
    }

    private static decimal Value(Price? price) => decimal.TryParse(price?.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var value) ? value : 0;
}

public sealed class T4ProbeTransport : IT4ProbeTransport
{
    private readonly ClientWebSocket _socket = new();
    public Task ConnectAsync(CancellationToken cancellationToken) => _socket.ConnectAsync(new(T4ConfigurationProbe.SimulatorEndpoint), cancellationToken);
    public Task SendAsync(byte[] message, CancellationToken cancellationToken) =>
        _socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true, cancellationToken);
    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Expected a binary protocol message.");
            if (message.Length + result.Count > 1024 * 1024) throw new InvalidDataException("Protocol message exceeded the size limit.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return message.ToArray();
    }
    public void Dispose() => _socket.Dispose();
}
