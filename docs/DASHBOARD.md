# Local paper dashboard

This is a single-user, local paper workspace. It never routes real orders. No T4 account,
market-data subscription or credentials are bundled. The **Market data** page configures
continuous T4 simulator quotes, optional historical backfill and a separate diagnostic check.
See [market-data setup](MARKET_DATA.md) for credentials, behavior and outstanding provider validation.

## Start

With the .NET 10 SDK installed, run from the repository root:

```powershell
./scripts/start-dashboard.ps1
```

Open `http://127.0.0.1:5080`. In another terminal, read the generated local key:

```powershell
Get-Content ./data/dashboard/dashboard.key
```

Paste it into the unlock form. The browser keeps the key in memory, not localStorage,
cookies, URLs, or source files. Locking/reloading removes it. Keep the server running;
the engine operates independently of the browser. Restarting pauses entries and clears
unfilled signals; recorded positions, protection, fills, risk baselines, and reports survive.

The server binds only to 127.0.0.1 and rejects remote IPs, foreign Host headers,
cross-origin requests, and unauthenticated API calls. Do not expose it through a tunnel
or reverse proxy. Public hosting needs TLS, user authentication/MFA, monitoring, and a
separate deployment/security review. GitHub Pages only hosts the public CSV preview/docs.

| Environment variable | Purpose |
|---|---|
| `Dashboard__Port` | Local port, default 5080 |
| `Dashboard__DataDirectory` | Private state directory, default repository `data/dashboard` |
| `Dashboard__AccessKey` | Optional operator key instead of the generated file; 32–256 characters |
| `MarketData__AdapterKey` | Separate 32–256 character key for configured external adapter ingress |
| `MarketData__T4__ApiKey` | Server-side T4 simulator API key for streaming and diagnostics |

Never commit credentials, state, or market data. `data/` is ignored. Custom state directories
should be outside version control and accessible only to your OS user. For continuous
operation, prefer a state directory outside OneDrive or other syncing software.

## Implemented behavior

- Responsive overview, five-minute candles, quote freshness, indicator/decision status,
  positions, simulated stops/targets, fills, activity, risk settings, and saved reports.
- Existing EMA20 reclaim signal; long-only, at most one open position per symbol.
- Warmup bars cannot trade. Each new completed-bar signal waits for a quote timestamp
  strictly after both the bar end and server receipt of the signal. Entry is ask plus
  slippage; marks use bid; exits use bid minus slippage.
- Planned stop-loss budget per trade, spread limit, position count, daily loss, peak
  drawdown, freshness checks, weekday entry schedule, and scheduled paper flatten.
- Stops and targets evaluated on accepted quotes even when entries are paused.
- A one-second monitor checks staleness/risk independently of one-second browser polling.
- TradingView Lightweight Charts with zoom/pan, crosshair, OHLC/volume readout, EMA overlays,
  5m/15m/1h display intervals and fullscreen. The strategy remains on completed five-minute bars.
- A separate [TradingView research page](TRADINGVIEW.md) links MES/MNQ futures charts and
  optionally embeds SPY/QQQ ETF context. Widget data never feeds paper execution.
- [Public.com ETF reference quotes](PUBLIC_API.md) with server-only authentication,
  individual last/bid/ask timestamps, stale labels, throttled refresh and sanitized errors.
- Atomic file snapshots are flushed before replacement/publication. Failed persistence
  blocks further mutations until restart. One writer per directory; corrupt state fails startup.
- CSV uploads go to the local server, which saves reports but not raw CSVs. Limits are
  2 MB / 5,000 bars per run and 100 saved reports. Replay never changes forward equity.

## Controls and recovery

| Control | Behavior |
|---|---|
| Resume | Requires fresh quotes, weekday entry window, no risk halt, no incomplete flatten |
| Pause entries | Clears pending signals; simulated exit protection remains active |
| Cancel pending entries | Clears signals without changing arming/protection |
| Flatten paper positions | Pauses, clears signals, closes with fresh bids; stale symbols wait |

Risk breaches latch a halt and request flatten. Default entries run weekdays 09:35–15:45
America/New_York; positions are scheduled to flatten outside that window. Daily loss
baselines reset at 18:00 ET, with DST support, rather than midnight. A session change can
clear a daily-loss halt; resuming is still explicit. Peak drawdown persists. Settings
cannot reset account equity or history and require flat, paused state. Holidays and early
closes require operator scheduling; an exchange holiday calendar is not implemented.

Stale quotes pause entries. The system never invents exit prices or claims stale exposure
closed. Pending flatten waits for fresh quotes. Local simulated protection stops if the
server stops; it is **not broker-held protection**. Restart requires new quotes and explicit
resume. Inspect recovered positions before resuming.

State: `paper-session.json`; previous snapshot: `.bak`. Back up after stopping the service.
For corrupt-state recovery, stop, preserve the corrupt file, inspect/restore a known-good
backup, then restart paused. An older backup can lose recent simulated activity. There is
no browser reset button that erases trading history.

## External provider adapter contract

Implement a server-side process that logs into its provider using secrets outside Git.
Send normalized JSON with `X-Adapter-Key`. Dashboard keys cannot inject data; adapter keys
cannot operate controls or read account reports. A missing adapter key returns 503. Select
External adapter in Market data and save complete exact contract bindings before sending data;
other provider modes return 409, and disabled symbols/wrong contracts return 400.

`POST /api/feed/quotes`:

```json
{"symbol":"MES","contractId":"PROVIDER-EXPIRING-CONTRACT-ID","timestamp":"2026-10-05T14:00:01Z","bid":5000.00,"ask":5000.25}
```

`POST /api/feed/bars`:

```json
{"contractId":"PROVIDER-EXPIRING-CONTRACT-ID","warmup":true,"bar":{"symbol":"MES","timestamp":"2026-10-05T14:00:00Z","open":5000.00,"high":5001.00,"low":4999.50,"close":5000.25,"volume":100}}
```

These prices are illustrative schema examples, not market data. Use an actual expiring
provider contract, never a continuous back-adjusted alias. Use decimal types and real
event timestamps with explicit offsets; never retimestamp old quotes to appear fresh.
Quotes must increase per symbol, align to ticks, and arrive within the freshness limit
(15 seconds by default, at most two seconds future skew). Only MES/MNQ are accepted.

Bars must be five-minute, end-stamped, completed, and strictly increasing per symbol.
Load warmup while paused; at least 50 contiguous bars are required. Gaps clear history;
contract changes require flat/paused state and clear that symbol's history. Indicators
recalculate over a maximum 512-bar window. Unmarked historical bars are rejected.
Serialize/deduplicate the adapter stream and handle non-2xx responses. Unfilled signals
expire after the quote timeout or are replaced by a newer bar.

Start T4 work with the [V2 API](https://docs.t4login.com/doku.php?id=developers:apiv2) and
[simulator/certification requirements](https://docs.t4login.com/doku.php?id=developers:apiv2:certification).
T4 diagnostic login, continuous quotes/ticker, reconnect, bar aggregation and optional historical
backfill are implemented. Actual-account verification, sustained-load validation, discovery,
calendars and certification are still required. See MARKET_DATA.md for the remaining limitations.

## Limits and next release

This snapshot store is for local paper research. Ordered quote batches reduce disk writes, but sustained production tick throughput has not been validated.
Skipped feed updates can miss simulated stops. The fill model assumes full quantity at
the accepted quote plus slippage. Stops can gap through their trigger; targets use simulated
market exits. No partial fills, order queues, market impact, margin model, broker working
orders, or broker reconciliation exists. A planned stop budget does not cap losses through gaps.
At 5,000 closed trades, new entries are blocked until a separately managed session archive
is prepared. The UI displays the latest 100 fills/trades and 200 activity events.

Historical replay retains legacy same-close fills and calendar-day exits. Saved reports
label those assumptions. It is separate from forward next-quote execution; data is not
independently verified and results do not establish profitability.

Next: implement/certify a provider adapter, validate forward operation against licensed data,
upgrade storage for sustained feeds, implement broker order lifecycle/reconciliation,
test broker protection, then add hosted authentication, alerts, backups/deployment/rollback,
and only later explicit live enablement.

## Verify

```powershell
dotnet build --configuration Release
dotnet test --configuration Release --no-build
./scripts/test-dashboard.ps1 -Configuration Release
```

HTTP tests use only the fabricated fixture and an isolated temporary account. They check
key separation, host/origin rejection, replay isolation, restart persistence, and duplicate
feed rejection. Unit tests cover next-quote fills, gap stops, stale flatten, restart recovery,
risk halts, session reset/DST, duplicate messages, spread/budget limits, and single-writer storage.
