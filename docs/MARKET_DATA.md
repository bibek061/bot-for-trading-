# MES/MNQ market data and charts

The local dashboard supports a **continuous T4 simulator feed**, authenticated historical
API backfill, exact expiring MES/MNQ bindings, external adapter ingress, and a TradingView
Lightweight Charts interface. Live broker order routing remains disabled.

The implementation is covered by scripted protocol/HTTP tests. **No authorized T4 account
or data key is available in this project yet**, so real provider behavior, entitlements,
load handling and timing still need simulator verification. No synthetic prices appear in
the dashboard and no subscription is purchased or activated by this software.

## Connect with your provider API key

1. Obtain T4 simulator/API access and authorized MES/MNQ market data.
2. Put the API key on the server using `MarketData__T4__ApiKey` or the ignored local file below.
   Restart the dashboard after changing a credential.
3. In **Market data**, select **T4 simulator**, enable the instruments, and enter their exact
   exchange, product and expiring market IDs. IDs come from T4; do not invent or split them.
4. Select the historical timestamp timezone confirmed for your T4 Chart API. T4 documents
   its offset-free chart timestamps as CST. Choose fixed CST (UTC−06:00) if that is what your
   provider supplies, or America/Chicago if it uses US Central time with DST. Until confirmed,
   backfill is disabled and candles are collected from new trades only.
5. Save settings and click **Connect feed**. The page reports connecting, backfilling,
   connected, streaming, waiting for quotes, reconnecting, or a specific failure.
6. Check prices, the selected expiry and indicator readiness before explicitly resuming
   paper entries. Connecting or reconnecting never arms the strategy.

| Dashboard field | T4 API field | Meaning |
|---|---|---|
| Instrument | Local symbol | MES or MNQ |
| Exchange ID | `exchange_id` | Provider exchange identifier |
| Product / contract ID | `contract_id` | Product, not the expiring instrument |
| Expiring market ID | `market_id` | Exact expiry, never a continuous-series alias |

Changing provider/contract settings requires a disconnected feed and a flat, paused session
without pending entries. Saving clears quotes, contract bindings and indicator history;
account baselines and recorded trades remain. Flatten using the old contract's fresh feed
before changing expiry. Automatic rollover and instrument discovery are not implemented.

## Server-only credentials

Create `config/appsettings.Local.json`, which is already ignored by Git:

```json
{
  "MarketData": {
    "T4": { "ApiKey": "" },
    "AdapterKey": ""
  }
}
```

Fill the relevant value locally. Do not paste keys into chat, the browser form, JavaScript,
Git, or logs. The file is plaintext; restrict it to your OS user. Environment and command-line
settings take precedence. The API exposes only credential-presence flags.

- `MarketData__T4__ApiKey`: T4 simulator API-key authentication. Password/SSO login is not implemented.
- `MarketData__AdapterKey`: optional independent key for external feed ingress; 32–256 characters,
  different from the operator key.

Nonsecret settings are saved atomically in `data/dashboard/market-data.json` (or the custom
state directory). No connection automatically resumes after a server restart. Test results
are in-memory observations; they are cleared on settings changes or restart. Invalid settings
fail startup instead of silently choosing another provider.

## Continuous connection behavior

The WebSocket endpoint is fixed to `wss://wss-sim.t4login.com/v2`; historical requests go only
to `https://api-sim.t4login.com/chart/barchart` with redirects disabled. Keys and short-lived
REST bearer tokens remain on the server. There are no account or order-routing message types
in the client protocol.

Every connection authenticates again, checks current exchange permissions, requests REAL
price format, and subscribes to top-of-book plus trade ticker. Definitions must match the
selected exchange/product, outright-future type, future last-trading date, tick size and
point value. Delayed data, invalid prices, expired contracts and missing permissions stop
admission to the paper engine.

A full snapshot establishes the top of book. Subsequent present bid/offer sides update the
book; absent sides retain their prior values. Quote timestamps come from the provider event,
not receipt time or a local timer. Reordered, crossed, stale and future quotes trigger recovery.
Trade counter gaps/resets trigger a fresh connection. Cached snapshot trades never enter
candle volume. Source book-update and counter semantics still need actual-account verification.

The client sends heartbeats every 20 seconds and treats a silent transport after 55 seconds
as disconnected. It bounds messages to 128 KiB and the processing queue to 128 messages.
Overflow stops the connection rather than silently dropping market events. Paper quotes are
processed in order, in batches of up to 128 events or approximately 100 ms, and saved once
per batch. Every accepted intermediate price still evaluates simulated stops and targets.
Historical warmup uses a single atomic batch per symbol. This reduces disk writes, but does
not establish production tick-throughput capacity; overload and outage tests remain required.

Disconnects and retries invalidate quotes/history, cancel entry intentions and pause the
strategy while retaining open positions and their protections. No exit is invented while
quotes are missing. Transient failures retry with 2/4/8/16-second delays, up to five attempts
per Connect action; authentication/permission/definition errors stop for operator correction.
The next successful connection reloads history when configured. Resume always remains explicit.

## Historical bars and live candles

With a confirmed timezone, the Chart API loads up to seven days of five-minute data and retains
at most 512 bars per symbol. Response sizes/counts, exact expiry, tick economics, OHLC, volume,
ordering and duplicate timestamps are checked. JSON price units are converted using the returned
minimum-price-increment definition. Offset-bearing timestamps are preserved; ambiguous or
nonexistent DST-local times are rejected rather than guessed.

`time` is the interval start. The completed-bar end is start plus five minutes, since `closeTime`
can represent the last trade before that boundary. The currently forming interval is excluded
from historical warmup. Historical bars cannot create orders.

While connected, each five-minute boundary requests a fresh REST token and refreshes completed
API bars in the background while quotes continue flowing. This fills the initial partial interval
and avoids losing the historical warmup at the live handoff. Duplicate bars are ignored; bars
that arrive too late for the signal freshness limit are warmup-only and pause entries. API failures
stop the connection and invalidate data. Feed/HTTP latency can therefore interrupt paper trading.

Without backfill, the ticker aggregates five-minute candles locally. The interval that was
already underway at connection time is excluded from completed strategy bars. At least 50
contiguous completed bars are needed for the strategy. There are no fabricated empty bars;
a time gap resets indicator history. Forming candles are display-only and never create signals.

## Chart features

The chart uses the locally hosted TradingView **Lightweight Charts 5.2.0** library with its
license and visible attribution. It has scroll zoom, drag pan, a price/time crosshair, OHLC and
volume readout, candles/line modes, EMA20/EMA50 overlays, volume, Fit/Latest controls and fullscreen.

The 5-minute, 15-minute and 1-hour selectors change display aggregation only. The strategy stays
on five-minute bars. Chart EMAs follow the selected display interval; aggregated edge candles
may be partial. The time axis is UTC. The chart explicitly distinguishes a forming candle,
completed history, current quotes, stale quotes and no data. The browser polls local state once
per second; the provider connection runs independently of the tab.

TradingView supplies the Overview chart library; the configured provider supplies the bot's
prices. The separate [TradingView page](TRADINGVIEW.md) offers futures chart links and an
optional hosted SPY/QQQ ETF reference widget. No widget prices enter the bot, and no remote
script executes in the authenticated dashboard document. TradingView has no public quote
API; its free widgets do not include CME futures. A licensed provider feed is still required.

## External adapters and local API

External adapters remain supported. Select **External adapter**, set enabled exact expiry IDs,
and configure the independent adapter key. The key alone does not enable ingress. Requests must
match an enabled binding. Selecting T4 prevents mixed external feed injections.

Use the schemas in [DASHBOARD.md](DASHBOARD.md#external-provider-adapter-contract). The local
feed's `contractId` is the expiring market ID: for T4, map **`market_id`**, not `contract_id`.

| Endpoint | Operator operation (`X-Dashboard-Key`) |
|---|---|
| `GET /api/market-data` | Settings, missing requirements, connection statistics and diagnostic result |
| `PUT /api/market-data` | Save provider/contracts/history timezone while disconnected and flat/paused |
| `POST /api/market-data/connect` | Start continuous T4 data using server credentials |
| `POST /api/market-data/disconnect` | Stop T4, invalidate data and pause entries |
| `POST /api/market-data/test` | Separate bounded diagnostic; continuous feed must be disconnected |

The optional diagnostic still signs in, checks definitions/current quotes and disconnects within
15 seconds. It does not start continuous data. Unknown form fields, browser-supplied API keys,
custom endpoints and malformed bindings are rejected. Missing configuration blocks any connection
before attempting network access. Live broker routing remains disabled throughout.

## References and remaining validation

- [T4 connection and authentication](https://docs.t4login.com/doku.php?id=developers:apiv2:connecting)
- [T4 streaming messages](https://docs.t4login.com/doku.php?id=developers:apiv2:markets)
- [T4 Chart API](https://docs.t4login.com/doku.php?id=developers:chart)
- [Official CTS protocol schemas](https://github.com/CTS-Futures/t4-api-tools/tree/main/proto/t4/v2)
- [TradingView Lightweight Charts](https://tradingview.github.io/lightweight-charts/docs)

The protobuf subset is independently defined for interoperability; generated C# is build output.
Before relying on the feed, validate actual T4 access, timezone/price normalization, subscriptions,
sequence behavior, history availability, reconnect/overload behavior and source licensing with the
provider. Exchange holiday calendars, automatic discovery/rollover, broker-held orders, execution
reconciliation and production deployment remain separate work. This release remains paper research.
