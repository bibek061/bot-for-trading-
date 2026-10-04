# Autopilot Quant

Experimental .NET 10 research prototype for MES/MNQ futures. It currently supports historical CSV replay, market-regime classification, a long-only EMA20 pullback/reclaim signal, risk-gated simulated orders, and paper-trade reporting.

## Safety and scope

- Paper simulation only. T4 market data and live order routing are not implemented.
- Short entries are locked by default.
- Each paper entry is an independent position that is closed by position ID.
- Replay uses explicit-price simulated fills, configured slippage/fees, and the configured trading timezone (default `America/New_York`).
- Replay closes open positions at timezone-local calendar-day boundaries and at the end of input.
- The synthetic CSV under the test project is fabricated and used only for automated tests; it is not historical market data or a performance result.
- No licensed market-data provider or API account is currently configured. The public website lets users preview a CSV selected from their own device; its JavaScript does not upload the file.
- Never put provider credentials in browser JavaScript, static website files, or Git; use local environment variables or server-side secret storage.
- Strategy thresholds are research baselines, not validated trading advice.

## Current components

- Configurable risk engine for data freshness, connection, open-position, daily-loss, and drawdown checks.
- ATR(14) / close risk-off classification and EMA(20)/EMA(50) trend/chop classification.
- Long-only entry candidate when an uptrend pulls back to and reclaims EMA20 on completed bars.
- Paper coordinator prevents duplicate/out-of-order bar processing and runs risk checks before entries.
- CSV replay runner sorts bars, applies configured timezone boundaries, simulates fills, and exports JSON run/trade reports.
- GitHub Pages page previews user-selected CSV files locally and documents the project limitations; it does not include market data or replay results.

## Historical replay

CSV columns: `Symbol,Timestamp,Open,High,Low,Close,Volume`. Timestamp values must be ISO 8601 with `Z` or an explicit UTC offset. Use data whose provider terms authorize your intended use.

    dotnet test
    dotnet run --project src\AutopilotQuant.Runner -- replay C:\path\to\licensed-bars.csv --report-json C:\path\to\replay-report.json

Use real historical data only when authorized and correctly licensed. Results are highly dependent on data quality and simplified execution assumptions; they do not establish profitability or predict future performance.

## Not implemented yet

- T4 quote ingestion, reconnect handling, contract rollover/discovery, and all live order routing.
- Persistent trade/replay storage, breakout strategy, learning/allocation, dashboard, monitoring, and deployment automation.

This project is experimental software, not financial advice, a broker, or a recommendation to trade. See the repository [README](../README.md) and [MIT License](../LICENSE).
