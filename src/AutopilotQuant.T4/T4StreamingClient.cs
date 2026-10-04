using System.Threading.Channels;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.T4.Protocol;
using Google.Protobuf;

namespace AutopilotQuant.T4;

public interface IT4FeedSink
{
    void State(string stage, string message);
    void Received();
    Task QuoteAsync(FeedQuote quote);
    async Task QuotesAsync(IReadOnlyList<FeedQuote> quotes) { foreach (var quote in quotes) await QuoteAsync(quote); }
    Task BarAsync(FeedBar bar);
    Task HistoryAsync(IReadOnlyList<FeedBar> bars);
    void Forming(MarketBar bar);
    Task InvalidateAsync(string reason);
}

public sealed class T4StreamingClient(T4HistoryClient history, Func<IT4ProbeTransport>? transportFactory = null, TimeProvider? clock = null)
{
    private sealed record HistoryBatch(T4Market Market, IReadOnlyList<MarketBar> Bars);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<IT4ProbeTransport> _transportFactory = transportFactory ?? (() => new T4ProbeTransport());

    public async Task RunAsync(T4Credentials credentials, IReadOnlyList<T4Market> markets, string historyTimeZone,
        int maxAge, IT4FeedSink sink, CancellationToken cancellationToken)
    {
        if (!credentials.Configured || markets.Count is < 1 or > 2)
            throw new T4FeedException("Complete T4 server credentials and selected contracts are required.", false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = lifetime.Token;
        using var transport = _transportFactory();
        using var sendGate = new SemaphoreSlim(1, 1);
        async Task Send(ClientMessage message)
        {
            await sendGate.WaitAsync(ct);
            try { await transport.SendAsync(message.ToByteArray(), ct); }
            finally { sendGate.Release(); }
        }
        sink.State("connecting", "Connecting to T4 simulator…");
        using var loginDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loginDeadline.CancelAfter(TimeSpan.FromSeconds(15));
        await transport.ConnectAsync(loginDeadline.Token);
        await transport.SendAsync(new ClientMessage { LoginRequest = credentials.CreateLoginRequest() }.ToByteArray(), loginDeadline.Token);
        LoginResponse? login = null;
        for (var count = 0; count < 32 && login is null; count++)
        {
            var frame = ServerMessage.Parser.ParseFrom(await transport.ReceiveAsync(loginDeadline.Token));
            login = frame.LoginResponse;
        }
        if (login is null || login.Result != 0 || string.IsNullOrWhiteSpace(login.SessionId))
            throw new T4FeedException("T4 simulator login was rejected. Check server credentials, application access and any required account action with CTS.", false);
        if (markets.Any(m => !login.Exchanges.Any(e => e.ExchangeId == m.ExchangeId && e.MarketDataType is 1 or 2 or 4)))
            throw new T4FeedException("Current market-data permission is missing for a selected exchange.", false);
        var channel = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(128) {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        Exception? backgroundFailure = null;
        async Task Receive()
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(55));
                    var bytes = await transport.ReceiveAsync(idle.Token);
                    if (bytes.Length > 128 * 1024) throw new T4FeedException("T4 message exceeded the streaming size limit.");
                    sink.Received();
                    if (!channel.Writer.TryWrite(ServerMessage.Parser.ParseFrom(bytes)))
                        throw new T4FeedException("Market-data processing fell behind. Reconnect and backfill required.");
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                backgroundFailure = ex; await lifetime.CancelAsync();
            }
            finally { channel.Writer.TryComplete(); }
        }
        async Task Heartbeats()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
                while (await timer.WaitForNextTickAsync(ct))
                    await Send(new() { Heartbeat = new() { Timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds() } });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                backgroundFailure = ex; await lifetime.CancelAsync();
            }
        }
        var receiver = Receive(); var heartbeats = Heartbeats();
        Task flushTimer = Task.CompletedTask;
        Task<HistoryBatch[]>? historyRefresh = null;
        var lastHistory = markets.ToDictionary(m => m.MarketId, _ => DateTimeOffset.MinValue);
        async Task<HistoryBatch[]> ReadHistory(string token)
        {
            var result = new List<HistoryBatch>();
            foreach (var market in markets)
                result.Add(new(market, await history.LoadAsync(market, token, historyTimeZone, _clock.GetUtcNow(), ct)));
            return result.ToArray();
        }
        try
        {
            if (historyTimeZone != "unconfirmed")
            {
                sink.State("backfilling", "Authenticated. Loading up to 512 completed five-minute bars per contract…");
                var token = login.AuthenticationToken;
                if (token is null || !token.HasToken || token.Token.Length == 0)
                {
                    const string requestId = "warmup";
                    await Send(new() { AuthenticationTokenRequest = new() { RequestId = requestId } });
                    using var tokenDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tokenDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                    while (true)
                    {
                        var frame = await channel.Reader.ReadAsync(tokenDeadline.Token);
                        if (frame.LoginResponse is { Result: not 0 }) throw new T4FeedException("T4 session ended during historical authorization.", false);
                        if (frame.AuthenticationToken is { } response && response.RequestId == requestId) { token = response; break; }
                    }
                }
                if (token is null || !token.HasToken || token.Token.Length == 0 || token.ExpireTime is null || token.ExpireTime.ToDateTimeOffset() <= _clock.GetUtcNow())
                    throw new T4FeedException("T4 did not issue a valid historical API token.", false);
                foreach (var batch in await ReadHistory(token.Token))
                {
                    ct.ThrowIfCancellationRequested();
                    await sink.HistoryAsync(batch.Bars.Select(bar => new FeedBar(batch.Market.MarketId, bar, true)).ToArray());
                    if (batch.Bars.Count > 0) lastHistory[batch.Market.MarketId] = batch.Bars[^1].Timestamp;
                }
            }
            var accumulators = markets.ToDictionary(m => m.MarketId, m => new T4MarketAccumulator(m, _clock.GetUtcNow()));
            var modes = new Dictionary<string, int>();
            var pendingQuotes = new List<FeedQuote>(128);
            async Task FlushQuotes()
            {
                if (pendingQuotes.Count == 0) return;
                ct.ThrowIfCancellationRequested();
                await sink.QuotesAsync(pendingQuotes.ToArray());
                pendingQuotes.Clear();
            }
            async Task FlushTicks()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await timer.WaitForNextTickAsync(ct))
                    channel.Writer.TryWrite(new ServerMessage()); // Internal flush signal, never a provider event or price.
            }
            var nextHistoryRefresh = T4MarketAccumulator.Floor(_clock.GetUtcNow()).AddMinutes(5);
            DateTimeOffset? tokenRequestedAt = null;
            foreach (var market in markets)
                await Send(new() { MarketSubscribe = new() { ExchangeId = market.ExchangeId, ContractId = market.ProductId,
                    MarketId = market.MarketId, Quotes = 1, Ticker = true } });
            sink.State("connected", historyTimeZone == "unconfirmed"
                ? "Connected. Waiting for current quotes; candles build from new trades. Select a historical timezone to enable backfill."
                : "Connected. History loaded; waiting for current contract definitions, snapshots and quotes.");
            flushTimer = FlushTicks();
            await foreach (var frame in channel.Reader.ReadAllAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                if (frame.PayloadCase == ServerMessage.PayloadOneofCase.None) await FlushQuotes();
                // Refresh completed API bars at each boundary while quotes keep flowing. This repairs
                // the partially observed startup interval instead of silently discarding history at the first gap.
                if (historyTimeZone != "unconfirmed")
                {
                    if (historyRefresh is { IsCompleted: true })
                    {
                        await FlushQuotes();
                        foreach (var batch in await historyRefresh)
                            foreach (var bar in batch.Bars.Where(b => b.Timestamp > lastHistory[batch.Market.MarketId]))
                            {
                                await sink.BarAsync(new(batch.Market.MarketId, bar, _clock.GetUtcNow() - bar.Timestamp > TimeSpan.FromSeconds(maxAge)));
                                lastHistory[batch.Market.MarketId] = bar.Timestamp;
                            }
                        historyRefresh = null;
                    }
                    if (tokenRequestedAt is { } requested && _clock.GetUtcNow() - requested > TimeSpan.FromSeconds(15))
                        throw new T4FeedException("T4 historical token refresh timed out.");
                    if (frame.AuthenticationToken is { RequestId: "history-refresh" } refreshed)
                    {
                        if (tokenRequestedAt is null || !refreshed.HasToken || refreshed.ExpireTime is null || refreshed.ExpireTime.ToDateTimeOffset() <= _clock.GetUtcNow())
                            throw new T4FeedException("T4 historical token refresh was not accepted.", false);
                        tokenRequestedAt = null;
                        historyRefresh = ReadHistory(refreshed.Token);
                    }
                    if (_clock.GetUtcNow() >= nextHistoryRefresh && historyRefresh is null && tokenRequestedAt is null)
                    {
                        await Send(new() { AuthenticationTokenRequest = new() { RequestId = "history-refresh" } });
                        tokenRequestedAt = _clock.GetUtcNow();
                        nextHistoryRefresh = T4MarketAccumulator.Floor(_clock.GetUtcNow()).AddMinutes(5);
                    }
                }
                if (frame.LoginResponse is not null) throw new T4FeedException("T4 session ended or requires a fresh login.");
                if (frame.MarketSubscribeReject is { } reject && accumulators.ContainsKey(reject.MarketId))
                    throw new T4FeedException("T4 rejected a selected market subscription. Check IDs and entitlements.", false);
                if (frame.MarketDetails is { } definition && accumulators.TryGetValue(definition.MarketId, out var defined))
                    defined.Verify(definition, _clock.GetUtcNow());
                if (frame.MarketSnapshot is { } snapshot && accumulators.TryGetValue(snapshot.MarketId, out var snapshotMarket))
                {
                    if (snapshot.Delayed) throw new T4FeedException("T4 supplied delayed market data. Current-data access is required.", false);
                    if (snapshot.DueToConnection && modes.ContainsKey(snapshot.MarketId))
                        throw new T4FeedException("Upstream connection recovery detected. Fresh login and backfill required.");
                    snapshotMarket.BeginSnapshot(_clock.GetUtcNow());
                    await Mode(snapshot.MarketId, snapshot.Mode);
                    foreach (var item in snapshot.Messages)
                        if (item.MarketDepth is { } depth && depth.MarketId == snapshot.MarketId) await Depth(depth);
                    // Snapshot trade prints are cached observations, not new incremental trades.
                }
                if (frame.MarketDepth is { } update) await Depth(update);
                if (frame.MarketTrade is { } trade && accumulators.TryGetValue(trade.MarketId, out var tradeMarket))
                {
                    await Mode(trade.MarketId, trade.Mode);
                    var completed = tradeMarket.Trade(trade, _clock.GetUtcNow(), maxAge);
                    if (completed is not null && historyTimeZone == "unconfirmed")
                    {
                        await FlushQuotes();
                        await sink.BarAsync(new(trade.MarketId, completed, _clock.GetUtcNow() - completed.Timestamp > TimeSpan.FromSeconds(maxAge)));
                    }
                    if (tradeMarket.FormingBar is { } forming) sink.Forming(forming);
                }
            }
            async Task Depth(MarketDepth depth)
            {
                if (!accumulators.TryGetValue(depth.MarketId, out var market)) return;
                await Mode(depth.MarketId, depth.Mode);
                if ((depth.Flags & (32 | 64 | 2048)) != 0) throw new T4FeedException("Market recovery or unavailable flags require a new snapshot.");
                if (market.Depth(depth, _clock.GetUtcNow(), maxAge) is { } quote)
                {
                    pendingQuotes.Add(quote);
                    if (pendingQuotes.Count == 128) await FlushQuotes();
                }
            }
            Task Mode(string marketId, int mode)
            {
                if (mode is 11 or 12 or 13 or 14 or 15) throw new T4FeedException("Selected market is expired, rejected or unavailable.", false);
                if (modes.TryGetValue(marketId, out var previous) && previous == 2 && mode != 2)
                    throw new T4FeedException("Market left its open state; invalidate data and reconnect.");
                modes[marketId] = mode;
                return Task.CompletedTask;
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(receiver, heartbeats, flushTimer); } catch (OperationCanceledException) { }
            if (historyRefresh is not null) { try { await historyRefresh; } catch (Exception) { /* Already stopping; never expose provider error text. */ } }
            if (!cancellationToken.IsCancellationRequested && backgroundFailure is not null)
                throw new T4FeedException("T4 transport stopped, timed out or exceeded the processing queue. Reconnect required.");
        }
    }
}
