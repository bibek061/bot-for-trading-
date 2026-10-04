using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.T4;

namespace AutopilotQuant.Web;

public sealed record T4StreamView(bool Active, string Stage, string Message, int Attempt, DateTimeOffset? LastMessageAt,
    long Messages, long Quotes, long CompletedBars, IReadOnlyList<MarketBar> FormingBars, DateTimeOffset? LastQuoteAt = null);

public sealed class T4MarketDataService(PaperSession session, T4StreamingClient client) : IHostedService, IT4FeedSink
{
    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _run;
    private int _maxQuoteAge = 15;
    private T4StreamView _view = new(false, "disconnected", "Connect your configured T4 feed to load prices.", 0, null, 0, 0, 0, []);
    public T4StreamView View()
    {
        lock (_stateLock)
        {
            var view = _view with { FormingBars = _view.FormingBars.ToArray() };
            return view.Active && view.Stage == "streaming" && (view.LastQuoteAt is null || DateTimeOffset.UtcNow - view.LastQuoteAt > TimeSpan.FromSeconds(_maxQuoteAge))
                ? view with { Stage = "waiting", Message = "T4 connection is open, but current quotes are unavailable or stale. Paper entries remain subject to freshness checks." } : view;
        }
    }
    public bool Active => View().Active;

    public async Task ConnectAsync(string apiKey, T4Market[] markets, string historyTimeZone, int maxAge)
    {
        await _control.WaitAsync();
        try
        {
            if (Active) throw new InvalidOperationException("T4 feed is already active. Disconnect before reconnecting.");
            var state = await session.ViewAsync();
            if (!state.Paused) throw new InvalidOperationException("Pause before connecting the T4 feed.");
            // Invalidation preserves any open exposure and protections, but prevents cached-quote fills.
            await session.InvalidateMarketDataAsync("T4 connecting; fresh quotes and explicit paper resume required.");
            _cancellation?.Dispose(); _cancellation = new();
            _maxQuoteAge = maxAge;
            lock (_stateLock) _view = new(true, "connecting", "Connecting to T4 simulator…", 1, null, 0, 0, 0, []);
            _run = RunAsync(apiKey, markets, historyTimeZone, maxAge, _cancellation.Token);
        }
        finally { _control.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _control.WaitAsync();
        try
        {
            if (_cancellation is not null) await _cancellation.CancelAsync();
            if (_run is not null) await _run;
            await session.InvalidateMarketDataAsync("T4 disconnected; paper entries paused. Open positions require fresh data.");
            lock (_stateLock) _view = _view with { Active = false, Stage = "disconnected", Message = "Disconnected. Paper strategy remains paused.", FormingBars = [] };
        }
        finally { _control.Release(); }
    }
    private async Task RunAsync(string apiKey, T4Market[] markets, string timeZone, int maxAge, CancellationToken ct)
    {
        try
        {
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                lock (_stateLock) _view = _view with { Attempt = attempt };
                try { await client.RunAsync(apiKey, markets, timeZone, maxAge, this, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Only our own fixed messages are surfaced; never expose provider payloads or tokens.
                    var message = ex is T4FeedException known ? known.Message : "T4 feed or historical response could not be verified. Check server configuration and provider access.";
                    await InvalidateAsync("T4 connection interrupted; fresh data and explicit paper resume required.");
                    if (ex is T4FeedException { Retryable: false } || ex is IOException or UnauthorizedAccessException || attempt == 5)
                    {
                        State("error", message + " Feed stopped; reconnect after resolving the issue."); break;
                    }
                    State("reconnecting", message + $" Retrying in {Math.Pow(2, attempt)} seconds; paper entries remain paused.");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { State("error", "Feed stopped after a local state failure. Restart after checking local storage."); }
        finally
        {
            try { await InvalidateAsync("T4 feed stopped; fresh market data required."); }
            catch (Exception) { State("error", "Local storage failed while stopping the feed. Restart required."); }
            lock (_stateLock) _view = _view with { Active = false, FormingBars = [] };
        }
    }
    public void State(string stage, string message) { lock (_stateLock) _view = _view with { Stage = stage, Message = message }; }
    public void Received() { lock (_stateLock) _view = _view with { Messages = _view.Messages + 1, LastMessageAt = DateTimeOffset.UtcNow }; }
    public async Task QuoteAsync(FeedQuote quote)
    {
        await session.AcceptQuoteAsync(quote);
        lock (_stateLock) _view = _view with { Quotes = _view.Quotes + 1, LastQuoteAt = quote.Timestamp, Stage = "streaming", Message = "Receiving T4 simulator prices. Paper entries require an explicit resume." };
    }
    public async Task QuotesAsync(IReadOnlyList<FeedQuote> quotes)
    {
        await session.AcceptQuotesAsync(quotes);
        lock (_stateLock) _view = _view with { Quotes = _view.Quotes + quotes.Count, LastQuoteAt = quotes.Max(q => q.Timestamp),
            Stage = "streaming", Message = "Receiving T4 simulator prices. Paper entries require an explicit resume." };
    }
    public async Task BarAsync(FeedBar bar)
    {
        var current = await session.ViewAsync();
        var last = current.Instruments.Single(i => i.Symbol == bar.Bar.Symbol).Bars.LastOrDefault();
        if (last is not null && bar.Bar.Timestamp <= last.Timestamp) return;
        if (bar.Warmup && !current.Paused) await session.ControlAsync("pause");
        await session.AcceptBarAsync(bar);
        lock (_stateLock) _view = _view with { CompletedBars = _view.CompletedBars + 1 };
    }
    public async Task HistoryAsync(IReadOnlyList<FeedBar> bars)
    {
        await session.LoadWarmupAsync(bars);
        lock (_stateLock) _view = _view with { CompletedBars = _view.CompletedBars + bars.Count };
    }
    public void Forming(MarketBar bar)
    {
        lock (_stateLock) _view = _view with { FormingBars = _view.FormingBars.Where(b => b.Symbol != bar.Symbol).Append(bar).ToArray() };
    }
    public async Task InvalidateAsync(string reason)
    {
        await session.InvalidateMarketDataAsync(reason);
        lock (_stateLock) _view = _view with { FormingBars = [], LastQuoteAt = null };
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => _run is null ? Task.CompletedTask : DisconnectAsync();
}
