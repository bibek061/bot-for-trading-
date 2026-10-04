# Autopilot Quant

Experimental futures research and paper-trading workspace for MES/MNQ, with a planned T4 integration.

## Local paper dashboard

The .NET web app provides a local dashboard, saved paper sessions, risk controls,
next-quote forward simulation, CSV replay reports, and authenticated ingress for a future
market-data adapter. The Market data page adds saved provider/contract configuration and
a T4 simulator stream with optional historical API backfill, reconnect handling and a
TradingView Lightweight Charts interface. Actual T4 account/entitlement verification is
still pending; no account or data subscription is bundled. Live order routing remains disabled.

The **TradingView** page adds MES/MNQ futures chart links and an optional hosted SPY/QQQ
reference widget with indicators and drawing tools. These ETFs are market context only:
TradingView offers no public quote API, and its free widgets do not include CME futures.
See [TradingView setup and data limits](docs/TRADINGVIEW.md).

The **Public.com market context** panel adds authenticated SPY/QQQ quote snapshots with
per-price timestamps, stale labels and optional 15-second refresh. Credentials stay on
the local server. [Public.com setup](docs/PUBLIC_API.md) is separate from futures execution.

```powershell
./scripts/start-dashboard.ps1
```

Open `http://127.0.0.1:5080`, then unlock with the key in `data/dashboard/dashboard.key`.
The service starts paused. Configure an authorized T4 simulator API key and contract IDs,
or use an external adapter, then check fresh quotes before enabling paper entries.
GitHub Pages cannot host this backend.
See [dashboard setup, integration, recovery, and limitations](docs/DASHBOARD.md).
See [market-data configuration and server credentials](docs/MARKET_DATA.md) to prepare T4
simulator access or bind a separate external adapter to exact expiring contracts.
The [T4 setup guide](docs/T4_SETUP.md) links the official GitHub API source and simulator
registration. Source downloads do not include an account's API key or data permissions.

## Current scope
- Windows + .NET 10
- MES and MNQ
- Long-only initially
- Short exposure locked until the rolling 30-day gate passes:
  - >= 12 closed trades
  - >= 60% win rate
  - >= 1.15 profit factor
- India NSE/BSE architecture reserved for a later, separately validated broker adapter
- DryRun by default. Live order routing is intentionally disabled.
- MIT-licensed; see [LICENSE](./LICENSE).
- Public project website: https://bibek061.github.io/bot-for-trading-/

## Intended flow
T4 market data -> regime detection -> strategies -> learning/allocation -> risk engine -> simulated/live broker -> performance feedback.

## Current status
The repository includes contract metadata, configurable risk checks, feature gate logic, a 5-minute-bar market regime classifier, a long-only EMA20 pullback/reclaim signal evaluator, and a paper-only coordinator that connects completed-bar signals to simulated orders. The coordinator processes each instrument's completed bars once, ignores out-of-order bars, marks existing positions to market, and enforces a configurable open-position limit. The initial regime baseline uses ATR(14) / close >= 1% for risk-off; otherwise, EMA(20) relative to EMA(50) and the latest EMA(20) slope classify uptrend, downtrend, or chop. The classifier returns insufficient-data until 50 bars are available. The signal evaluator emits a BUY candidate only when an uptrend's preceding completed bar closed at/below EMA20 and the current completed bar closes above EMA20. These thresholds and rules are initial paper-research assumptions and are not validated trading advice.

Each paper entry is tracked as an independent position and must be closed by its position ID. Short entries remain locked by default. The simulator tracks mark-to-market unrealized P/L, closed-trade realized P/L, and entry/exit fees. CLI replay is isolated per run; the web forward session persists state and resumes paused after restart. A server-side T4 simulator client can deliver authorized quotes and completed bars to the forward engine, alongside the authenticated external feed boundary. Configure and verify provider access before use. Live order routing remains disabled.

## Use your own market data

The [project website](https://bibek061.github.io/bot-for-trading-/) lets you select a CSV from your device to preview its bars in your browser. The selected file is not uploaded or sent to a server. The public page does not contain market prices, replay results, or a provider API. Provider credentials belong only in the local dashboard's server configuration.

Use a market-data provider whose terms authorize your intended use; keep any provider credentials local or server-side, never in Git or browser code. The fabricated CSV under `tests/AutopilotQuant.Tests/Fixtures/` exists only to make automated tests repeatable and is not used by the site.

## Next milestones
1. Obtain authorized T4 simulator/data access and validate the implemented streaming/backfill path
2. Verify normalization, reconnect and sustained throughput; add discovery and exchange calendars
3. Validate EMA20 research and forward fills on licensed market data
4. Upgrade local snapshots for sustained feeds and durable operational auditing
5. Implement and test broker order lifecycle, protective orders, and reconciliation
6. Hosted authentication/MFA, external alerts, backups, deployment/rollback
7. Only after validation and certification: explicit live execution enablement
8. Additional strategies and allocation after the execution foundation is reliable

## Run

From PowerShell:

    cd C:\Projects\AutopilotQuant
    dotnet restore
    dotnet test
    dotnet run --project src\AutopilotQuant.Runner

Historical paper replay expects CSV columns `Symbol,Timestamp,Open,High,Low,Close,Volume`, with timestamps as ISO 8601 values including an explicit timezone offset:

    dotnet run --project src\AutopilotQuant.Runner -- replay C:\path\to\licensed-bars.csv --report-json C:\path\to\replay-report.json

The replay runs locally against the selected file. Replay orders are simulated only. Open positions are closed at each replay trading-day boundary in `America/New_York` (configurable) and at the end of the input, using the last available bar close and configured paper slippage/fees. The replay sorts bars chronologically and rejects duplicate symbol/timestamp pairs.

The JSON report contains run-level counts and performance metrics plus each closed trade's entry/exit prices, fees, gross P/L, and net P/L. Win rate uses net trade P/L. Profit factor uses net winning and losing trade P/L and is `null` when there are no losing trades because the ratio is undefined.

## Release and research disclaimer

This is an experimental paper-trading/research prototype, not financial advice, a brokerage service, or a recommendation to trade. Strategies and thresholds have not established profitability. The test fixture is fabricated, not exchange data. The T4 simulator data client awaits actual-account validation; live order routing is not implemented. Paper replay simplifies execution and does not model queue position, partial fills, market impact, or all fees and outages. Historical results do not guarantee future performance.
